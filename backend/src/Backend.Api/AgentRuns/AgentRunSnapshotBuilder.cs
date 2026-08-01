using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Common;
using Backend.Api.Agents;
using Backend.Api.Skills;
using YamlDotNet.Serialization;

namespace Backend.Api.AgentRuns;

internal static class AgentRunSnapshotBuilder
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    internal sealed record Built(
        string StoredSnapshot,
        string SnapshotHash,
        byte[] CanonicalBytes,
        int EffectiveTimeoutSeconds)
    {
        public int CanonicalByteLength => CanonicalBytes.Length;
    }

    public static IReadOnlyList<AgentValidationError> ValidateExecutionContract(
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        IReadOnlyList<SkillSnapshotSource> skills,
        string tenantId,
        string userId,
        string role)
        => ValidateExecutionContract(
            agent,
            workflow,
            skills,
            tenantId,
            userId,
            role,
            Array.Empty<string>());

    public static IReadOnlyList<AgentValidationError> ValidateExecutionContract(
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        IReadOnlyList<SkillSnapshotSource> skills,
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups)
    {
        var errors = AgentCanonicalizer.Validate(agent.Definition, agent.Name).ToList();
        ValidateIdentity("caller.tenant_id", tenantId, AgentExecutionContract.MaxCallerIdentityLength, errors);
        ValidateIdentity("caller.user_id", userId, AgentExecutionContract.MaxCallerIdentityLength, errors);
        ValidateIdentity("caller.role", role, AgentExecutionContract.MaxCallerRoleLength, errors);
        if (!AgentAudience.IsCanonicalGroupSet(groups))
        {
            errors.Add(new AgentValidationError(
                "caller.groups",
                "caller.groups must be a unique bounded set of canonical group ids"));
        }

        if (skills.Count > AgentExecutionContract.MaxSkillBindings)
        {
            errors.Add(new AgentValidationError(
                "skills", $"skills 最多 {AgentExecutionContract.MaxSkillBindings} 筆"));
        }
        if (skills.Select(skill => (skill.Name, skill.Revision)).Distinct().Count() != skills.Count)
        {
            errors.Add(new AgentValidationError("skills", "skill revision pins 不可重複"));
        }
        foreach (var skill in skills)
        {
            ValidateIdentity(
                "skills.name", skill.Name, AgentExecutionContract.MaxSkillNameLength, errors);
            if (skill.Description.Length > AgentExecutionContract.MaxSkillDescriptionLength)
            {
                errors.Add(new AgentValidationError(
                    "skills.description",
                    $"skill description 不可超過 {AgentExecutionContract.MaxSkillDescriptionLength} 字元"));
            }
            if (skill.Revision < 1 || skill.Kind is not ("agentic" or "flow"))
            {
                errors.Add(new AgentValidationError(
                    "skills", $"Skill pin 無法由 D3 runtime 載入：{skill.Name}#{skill.Revision}"));
            }
        }

        if (workflow.Revision < 1
            || string.IsNullOrWhiteSpace(workflow.CompilerContractVersion)
            || workflow.CompilerContractVersion.Length
            > AgentExecutionContract.MaxWorkflowContractVersionLength)
        {
            errors.Add(new AgentValidationError(
                "runtime_workflow", "Workflow execution snapshot contract 無效"));
        }
        return errors;
    }

    public static Built Build(
        Guid runId,
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> capabilityClaims,
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        IReadOnlyList<SkillSnapshotSource> skills,
        string executionKind = "direct-worker",
        int? orchestratorTokenCap = null,
        OrchestratorChildSnapshotProvenance? orchestratorProvenance = null)
        => Build(
            runId,
            tenantId,
            userId,
            role,
            Array.Empty<string>(),
            capabilityClaims,
            agent,
            workflow,
            skills,
            executionKind,
            orchestratorTokenCap,
            orchestratorProvenance);

    public static Built Build(
        Guid runId,
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        IReadOnlyList<SkillSnapshotSource> skills,
        string executionKind = "direct-worker",
        int? orchestratorTokenCap = null,
        OrchestratorChildSnapshotProvenance? orchestratorProvenance = null)
    {
        if (executionKind is not ("direct-worker" or "orchestrator-worker" or "orchestrator-verifier"))
        {
            throw new ArgumentOutOfRangeException(nameof(executionKind));
        }
        var definition = JsonNode.Parse(agent.Definition)!.AsObject();
        var runtimeLimits =
            definition["runtime_limits"]?.DeepClone() as JsonObject ?? new JsonObject();
        if (orchestratorTokenCap is not null)
        {
            if (executionKind is not ("orchestrator-worker" or "orchestrator-verifier")
                || orchestratorTokenCap is < 1 or > AgentExecutionContract.MaxOrchestratorTokenCap
                || orchestratorProvenance is null
                || orchestratorProvenance.RootRunId == Guid.Empty
                || string.IsNullOrWhiteSpace(orchestratorProvenance.TaskId)
                || orchestratorProvenance.Attempt < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(orchestratorTokenCap));
            }
            var configuredTokenBudget = runtimeLimits["token_budget"]?.GetValue<int>() ?? 0;
            var effectiveTokenBudget = configuredTokenBudget > 0
                ? configuredTokenBudget
                : AgentExecutionContract.DefaultTokenBudget;
            // Both this materialised limit and the separate cap are immutable snapshot
            // input. Workflow independently applies the same minimum at execution time.
            runtimeLimits["token_budget"] = Math.Min(effectiveTokenBudget, orchestratorTokenCap.Value);
        }
        var configuredTimeout = runtimeLimits["timeout_seconds"]?.GetValue<int>() ?? 0;
        var effectiveTimeout = configuredTimeout == 0
            ? AgentExecutionContract.DefaultTimeoutSeconds
            : configuredTimeout;
        runtimeLimits["effective_timeout_seconds"] = effectiveTimeout;
        var allowedTools = definition["allowed_tools"]?.DeepClone() ?? new JsonArray();
        var knowledgeSources = definition["knowledge_sources"]?.DeepClone() ?? new JsonArray();
        var callerClaims = AgentRunCapabilityClaims.Parse(capabilityClaims);
        var toolGrants = Intersect(allowedTools, callerClaims.ToolNames);
        var knowledgeSourceGrants = Intersect(
            knowledgeSources,
            callerClaims.KnowledgeSourceIds);

        var skillArray = new JsonArray();
        foreach (var skill in skills.OrderBy(s => agent.SkillBindings
                     .First(b => b.Skill == s.Name).Position))
        {
            skillArray.Add(new JsonObject
            {
                ["name"] = skill.Name,
                ["revision"] = skill.Revision,
                ["kind"] = skill.Kind,
                ["description"] = skill.Description,
                ["definition_sha256"] = skill.DefinitionSha256,
                ["package_sha256"] = skill.PackageSha256,
                // Exact allowed-tools is parsed from the immutable artifact by Workflow.
                ["allowed_tools"] = new JsonArray(),
            });
        }

        var agentNode = new JsonObject
        {
            ["id"] = agent.AgentId,
            ["revision"] = agent.Revision,
            ["name"] = agent.Name,
            ["system_prompt"] = definition["system_prompt"]?.DeepClone() ?? string.Empty,
            ["execution_roles"] = definition["execution_roles"]?.DeepClone() ?? new JsonArray(),
            ["audience"] = definition["audience"]?.DeepClone() ?? new JsonArray(),
            ["output_contract"] = definition["output_contract"]?.DeepClone() ?? new JsonObject(),
            ["business_rules"] = definition["business_rules"]?.DeepClone() ?? new JsonObject(),
            ["allowed_tools"] = allowedTools.DeepClone(),
            ["knowledge_sources"] = knowledgeSources.DeepClone(),
            ["runtime_limits"] = runtimeLimits,
        };
        // P1 optional pin (plan 03 §3): present only when the published Agent revision pinned a
        // prompt manifest. Absent entirely otherwise, so an unpinned snapshot stays byte-for-byte
        // identical to the pre-P1 shape.
        if (agent.PromptManifestRevision is int pinnedManifestRevision
            && agent.PromptManifestSha256 is string pinnedManifestSha256)
        {
            agentNode["prompt_manifest"] = new JsonObject
            {
                ["revision"] = pinnedManifestRevision,
                ["sha256"] = pinnedManifestSha256,
            };
        }

        var snapshot = new JsonObject
        {
            ["run_id"] = runId,
            ["agent"] = agentNode,
            ["workflow"] = new JsonObject
            {
                ["id"] = workflow.WorkflowId,
                ["revision"] = workflow.Revision,
                ["definition"] = JsonNode.Parse(workflow.Definition),
                ["definition_sha256"] = workflow.DefinitionSha256,
                ["compiler_contract_version"] = workflow.CompilerContractVersion,
            },
            ["skills"] = skillArray,
            ["caller"] = new JsonObject
            {
                ["tenant_id"] = tenantId,
                ["user_id"] = userId,
                ["role"] = role,
                ["groups"] = new JsonArray(
                    groups
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(group => group, StringComparer.Ordinal)
                        .Select(group => JsonValue.Create(group))
                        .ToArray()),
                // v1 grants are the exact intersection of persisted caller capability claims and
                // immutable Agent scope. Role (including ADMIN) never grants either dimension.
                ["tool_grants"] = toolGrants,
                ["knowledge_source_grants"] = knowledgeSourceGrants,
            },
            ["mode"] = "test",
            ["execution_kind"] = executionKind,
        };
        if (orchestratorTokenCap is not null)
        {
            snapshot["orchestrator_token_cap"] = orchestratorTokenCap.Value;
            snapshot["orchestrator_root_run_id"] = orchestratorProvenance!.RootRunId;
            snapshot["orchestrator_task_id"] = orchestratorProvenance.TaskId;
            snapshot["orchestrator_attempt"] = orchestratorProvenance.Attempt;
        }

        var stored = CanonicalJsonTree.Normalize(snapshot)!.ToJsonString(CanonicalJson);
        var canonicalBytes = Encoding.UTF8.GetBytes(stored);
        return new Built(
            stored,
            SkillHash.Sha256(canonicalBytes),
            canonicalBytes,
            effectiveTimeout);
    }

    /// <summary>D5 root artifact.  It intentionally has no <c>agent</c>: worker/verifier agents
    /// are pinned in the immutable Orchestrator definition, and are materialised only as child
    /// runs by Workflow.  This prevents a root coordinator from being mistaken for a Worker.</summary>
    public static Built BuildOrchestratorRoot(
        Guid runId,
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        Guid orchestratorId,
        int orchestratorRevision,
        string orchestratorDefinition,
        string orchestratorDefinitionSha256,
        WorkflowSnapshotSource rootWorkflow,
        IReadOnlyList<PublishedAgentSnapshotSource> workers,
        PublishedAgentSnapshotSource verifier,
        string rootMessage)
    {
        var definition = JsonNode.Parse(orchestratorDefinition)?.AsObject()
            ?? throw new InvalidOperationException("Orchestrator canonical definition is invalid");
        var budgets = definition["budgets"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Orchestrator budgets are missing");
        var timeout = budgets["timeoutSeconds"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Orchestrator timeoutSeconds is missing");
        var workflow = definition["workflow"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Orchestrator workflow is missing");
        var policy = definition["policy"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Orchestrator policy is missing");
        var context = definition["context"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Orchestrator context policy is missing");
        var rules = new JsonObject { ["version"] = 1, ["rules"] = new JsonArray() };
        var canonicalRules = CanonicalJsonTree.Normalize(rules)!.ToJsonString(CanonicalJson);
        var canonicalPolicy = CanonicalJsonTree.Normalize(policy)!.ToJsonString(CanonicalJson);
        var callerGrants = AgentRunCapabilityClaims.Parse(capabilityClaims);
        var workerPins = new JsonArray(workers.Select(ToRootWorkerPin).ToArray());
        var snapshot = new JsonObject
        {
            ["root_run_id"] = runId.ToString("D"),
            ["orchestrator_id"] = orchestratorId.ToString("D"),
            ["orchestrator_revision"] = orchestratorRevision,
            ["workflow_id"] = rootWorkflow.WorkflowId.ToString("D"),
            ["workflow_revision"] = rootWorkflow.Revision,
            ["graph"] = new JsonObject
            {
                ["definition"] = JsonNode.Parse(rootWorkflow.Definition),
                ["definition_sha256"] = rootWorkflow.DefinitionSha256,
                ["compiler_contract_version"] = rootWorkflow.CompilerContractVersion,
                ["runtime_adapter_version"] = "root-runtime-adapter-1",
                ["runtime_adapter_sha256"] = "a9fc6c65dcc0daaedd71bb7bb64e2d2bc1b65cbda5f9715880846df2a32bee63",
            },
            ["caller"] = new JsonObject
            {
                ["tenant_id"] = tenantId,
                ["user_id"] = userId,
                ["role"] = role,
                ["groups"] = new JsonArray(groups.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["tool_grants"] = new JsonArray(callerGrants.ToolNames.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["knowledge_grants"] = new JsonArray(callerGrants.KnowledgeSourceIds.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            },
            // Restart-safe root input. The observed timestamp is server-owned; callers never
            // get to inject transient context/memory into a recoverable root command.
            ["root_input"] = new JsonObject
            {
                ["message"] = rootMessage,
                ["observed_at"] = DateTimeOffset.UtcNow.ToString("O"),
            },
            ["authority"] = new JsonObject
            {
                ["context_tools"] = context["allowedTools"]?.DeepClone() ?? new JsonArray(),
                ["knowledge_sources"] = context["knowledgeSources"]?.DeepClone() ?? new JsonArray(),
                ["rule_set_sha256"] = SkillHash.Sha256(canonicalRules),
                ["policy_sha256"] = SkillHash.Sha256(canonicalPolicy),
            },
            ["workers"] = workerPins,
            ["verifier"] = ToRootWorkerPin(verifier),
            ["join_policy"] = policy["joinPolicy"]?.DeepClone(),
            ["limits"] = new JsonObject
            {
                ["max_context_rounds"] = budgets["maxContextRounds"]?.DeepClone(),
                ["max_tasks"] = budgets["maxTasks"]?.DeepClone(),
                ["max_child_runs"] = budgets["maxChildRuns"]?.DeepClone(),
                ["max_concurrency"] = budgets["maxConcurrency"]?.DeepClone(),
                ["max_repair_rounds"] = budgets["maxRepairRounds"]?.DeepClone(),
                ["timeout_seconds"] = budgets["timeoutSeconds"]?.DeepClone(),
            },
            ["token_budget"] = budgets["tokenBudget"]?.DeepClone(),
            ["context_byte_budget"] = 65_536,
            ["business_rules"] = rules,
            ["policies"] = policy,
        };
        var payload = CanonicalJsonTree.Normalize(snapshot)!.ToJsonString(CanonicalJson);
        var hash = SkillHash.Sha256(payload);
        snapshot["snapshot_hash"] = hash;
        var stored = CanonicalJsonTree.Normalize(snapshot)!.ToJsonString(CanonicalJson);
        return new Built(stored, hash, Encoding.UTF8.GetBytes(stored), timeout);
    }

    private static JsonObject ToRootWorkerPin(PublishedAgentSnapshotSource source)
    {
        var definition = JsonNode.Parse(source.Definition)?.AsObject()
            ?? throw new InvalidOperationException("Pinned Agent definition is invalid");
        return new JsonObject
        {
            ["agent_id"] = source.AgentId.ToString("D"),
            ["agent_revision"] = source.Revision,
            ["workflow_id"] = source.WorkflowId.ToString("D"),
            ["workflow_revision"] = source.WorkflowRevision,
            // This is the immutable Agent revision artifact hash, not a future per-child run hash.
            ["snapshot_hash"] = source.DefinitionSha256,
            ["token_cap"] = EffectiveOrchestratorTokenCap(definition),
            ["capabilities"] = definition["capabilities"]?.DeepClone() ?? new JsonArray(),
            ["read_only"] = true,
            ["skill_revisions"] = new JsonObject(source.SkillBindings
                .Where(binding => binding.Enabled)
                .OrderBy(binding => binding.Skill, StringComparer.Ordinal)
                .Select(binding => new KeyValuePair<string, JsonNode?>(
                    binding.Skill, JsonValue.Create(binding.SkillRevision)))),
        };
    }

    private static int EffectiveOrchestratorTokenCap(JsonObject definition)
    {
        var configured = definition["runtime_limits"]?["token_budget"]?.GetValue<int>() ?? 0;
        var effective = configured > 0
            ? configured
            : AgentExecutionContract.DefaultTokenBudget;
        return Math.Min(effective, AgentExecutionContract.MaxOrchestratorTokenCap);
    }

    public static string CreateExecutionArtifact(
        string canonicalSnapshot,
        string snapshotHash)
        => CreateExecutionArtifact(
            Encoding.UTF8.GetBytes(canonicalSnapshot),
            snapshotHash);

    public static string CreateExecutionArtifact(
        byte[]? canonicalBytes,
        string? snapshotHash)
    {
        _ = ReadAuthoritativeSnapshot(canonicalBytes, snapshotHash);

        var canonicalBase64 = Convert.ToBase64String(canonicalBytes!);
        if (canonicalBase64.Length > AgentExecutionContract.MaxSnapshotCanonicalBase64Length)
        {
            throw new InvalidOperationException(
                $"Agent run snapshot base64 exceeds {AgentExecutionContract.MaxSnapshotCanonicalBase64Length} characters");
        }

        return new JsonObject
        {
            ["snapshot_hash"] = snapshotHash!,
            ["snapshot_canonical_base64"] = canonicalBase64,
        }.ToJsonString(CanonicalJson);
    }

    public static string ReadAuthoritativeSnapshot(
        byte[]? canonicalBytes,
        string? snapshotHash)
    {
        if (canonicalBytes is null)
        {
            throw new InvalidOperationException(
                "Agent run lacks authoritative canonical snapshot bytes");
        }
        if (canonicalBytes.Length > AgentExecutionContract.MaxSnapshotCanonicalBytes)
        {
            throw new InvalidOperationException(
                $"Agent run snapshot exceeds {AgentExecutionContract.MaxSnapshotCanonicalBytes} canonical UTF-8 bytes");
        }
        if (!SkillHash.MatchesSha256(canonicalBytes, snapshotHash))
        {
            throw new InvalidOperationException("Agent run snapshot hash mismatch");
        }
        if (canonicalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidOperationException(
                "Agent run canonical snapshot must not contain a UTF-8 BOM");
        }

        try
        {
            var snapshot = StrictUtf8.GetString(canonicalBytes);
            using var document = JsonDocument.Parse(canonicalBytes);
            if (!HasSnapshotShape(document.RootElement))
            {
                throw new InvalidOperationException(
                    "Agent run canonical snapshot has an invalid root schema");
            }
            if (!string.Equals(
                    CanonicalizeJson(snapshot),
                    snapshot,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent run snapshot bytes are not canonical JSON");
            }
            return snapshot;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
        {
            throw new InvalidOperationException(
                "Agent run canonical snapshot is not strict UTF-8 JSON",
                ex);
        }
    }

    public static string CanonicalizeJson(string json)
        => CanonicalJsonTree.Normalize(JsonNode.Parse(json))!.ToJsonString(CanonicalJson);

    public static string SkillDescriptionOf(string immutableDefinition)
    {
        try
        {
            if (Yaml.Deserialize<object>(immutableDefinition)
                is IDictionary<object, object> root)
            {
                foreach (var pair in root)
                {
                    if (string.Equals(pair.Key?.ToString(), "description", StringComparison.Ordinal)
                        && pair.Value?.ToString()?.Trim() is { Length: > 0 } description)
                    {
                        return description;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Immutable Skill revision description cannot be parsed",
                ex);
        }

        throw new InvalidOperationException(
            "Immutable Skill revision is missing description");
    }

    private static void ValidateIdentity(
        string field,
        string value,
        int max,
        ICollection<AgentValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            errors.Add(new AgentValidationError(
                field, $"{field} 必須為 1 到 {max} 個字元"));
        }
    }

    private static JsonArray Intersect(JsonNode pinnedValues, IReadOnlySet<string> claims)
    {
        var result = new JsonArray();
        if (pinnedValues is not JsonArray pinned)
        {
            return result;
        }
        foreach (var item in pinned)
        {
            if (item?.GetValue<string>() is { } value && claims.Contains(value))
            {
                result.Add(value);
            }
        }
        return result;
    }

    private static bool HasSnapshotShape(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && HasKind(root, "run_id", JsonValueKind.String)
           && HasKind(root, "agent", JsonValueKind.Object)
           && HasKind(root, "workflow", JsonValueKind.Object)
           && HasKind(root, "skills", JsonValueKind.Array)
           && HasKind(root, "caller", JsonValueKind.Object)
           && HasKind(root, "mode", JsonValueKind.String);

    private static bool HasKind(
        JsonElement root,
        string property,
        JsonValueKind expected)
        => root.TryGetProperty(property, out var value)
           && value.ValueKind == expected;
}
