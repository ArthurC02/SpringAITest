using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        IReadOnlyList<SkillSnapshotSource> skills)
        => Build(
            runId,
            tenantId,
            userId,
            role,
            Array.Empty<string>(),
            capabilityClaims,
            agent,
            workflow,
            skills);

    public static Built Build(
        Guid runId,
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        IReadOnlyList<SkillSnapshotSource> skills)
    {
        var definition = JsonNode.Parse(agent.Definition)!.AsObject();
        var runtimeLimits =
            definition["runtime_limits"]?.DeepClone() as JsonObject ?? new JsonObject();
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

        var snapshot = new JsonObject
        {
            ["run_id"] = runId,
            ["agent"] = new JsonObject
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
            },
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
        };

        var stored = Canonicalize(snapshot).ToJsonString(CanonicalJson);
        var canonicalBytes = Encoding.UTF8.GetBytes(stored);
        return new Built(
            stored,
            SkillHash.Sha256(canonicalBytes),
            canonicalBytes,
            effectiveTimeout);
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
        => Canonicalize(JsonNode.Parse(json)!).ToJsonString(CanonicalJson);

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

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(
                    p.Key,
                    p.Value is null ? null : Canonicalize(p.Value)))),
        JsonArray arr => new JsonArray(
            arr.Select(item => item is null ? null : Canonicalize(item)).ToArray()),
        _ => node.DeepClone(),
    };

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
