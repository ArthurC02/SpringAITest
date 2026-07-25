using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Agents;

/// <summary>
/// Backend-owned bounds for the immutable D3 Agent execution snapshot. These values mirror
/// Workflow's strict runtime models; contract-vector tests guard against cross-service drift.
/// </summary>
public static class AgentExecutionContract
{
    public const int MaxCanonicalDefinitionBytes = 4 * 1024 * 1024;
    public const int MaxSnapshotCanonicalBytes = 8 * 1024 * 1024;
    public const int MaxSnapshotCanonicalBase64Length = 11_184_812;
    public const int MaxAgentNameLength = 256;
    public const int MaxSystemPromptLength = 200_000;
    public const int MaxExecutionRoles = 16;
    public const int MaxAudience = 256;
    public const int MaxAllowedTools = 256;
    public const int MaxKnowledgeSources = 512;
    public const int MaxSkillBindings = 128;
    public const int MaxToolRounds = 1_000;
    public const int MaxContextRounds = 1_000;
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 86_400;
    public const int MaxTokenBudget = 10_000_000;
    public const int MaxStepBudget = 10_000;
    public const int MaxCallerIdentityLength = 256;
    public const int MaxCallerRoleLength = 64;
    public const int MaxSkillNameLength = 64;
    public const int MaxSkillDescriptionLength = 4_096;
    public const int MaxWorkflowContractVersionLength = 128;
}

/// <summary>
/// Agent 定義的 canonicalize 與最小結構驗證。所有集合欄位缺席/null/空 → 明確空陣列
/// (fail closed,禁止 null=unrestricted,02-spec §2.1/§7.2);business_rules 保留呼叫端 AST，
/// 並和其他 JSON 欄位一樣遞迴排序 object key；完整 Rule AST 語意仍只由 Workflow 驗證。
/// key 以固定順序輸出 → canonical 文字可重現、可雜湊。
/// name/description/slug 是 agent 欄位而非定義內容,不入 canonical 定義。
/// </summary>
public static class AgentCanonicalizer
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> ValidExecutionRoles =
        new(StringComparer.Ordinal) { "worker", "verifier" };

    /// <summary>由請求建出 canonical 定義 JSON 原文(不含 name/description/slug)。</summary>
    public static string Canonicalize(AgentUpsert req)
    {
        var obj = new JsonObject
        {
            ["system_prompt"] = req.SystemPrompt ?? string.Empty,
            ["execution_roles"] = ToSetArray(req.ExecutionRoles),
            ["capabilities"] = ToSetArray(req.Capabilities),
            ["output_contract"] = CanonicalizeNode(ToNode(req.OutputContract)) ?? new JsonObject(),
            ["audience"] = ToAudienceArray(req.Audience),
            ["allowed_tools"] = ToSetArray(req.AllowedTools),
            ["skill_bindings"] = ToBindings(req.SkillBindings),
            ["knowledge_sources"] = ToKnowledgeSourceArray(req.KnowledgeSources),
            ["business_rules"] = CanonicalizeNode(ToNode(req.BusinessRules))
                                 ?? JsonNode.Parse(AgentDefaults.EmptyBusinessRules),
            ["runtime_limits"] = ToLimits(req.RuntimeLimits),
            ["runtime_workflow"] = ToWorkflow(req.RuntimeWorkflow),
        };
        return CanonicalizeDefinition(obj.ToJsonString());
    }

    /// <summary>由 canonical 定義取出已固定(去重、依序)的 skill binding 名稱清單(供 publish 解析為 revision)。</summary>
    public static IReadOnlyList<AgentSkillBinding> SkillBindingsOf(string canonicalDefinition)
    {
        var node = JsonNode.Parse(canonicalDefinition)?["skill_bindings"]?.AsArray();
        if (node is null)
        {
            return Array.Empty<AgentSkillBinding>();
        }

        var result = new List<AgentSkillBinding>();
        foreach (var item in node)
        {
            var skill = item?["skill"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(skill))
            {
                result.Add(new AgentSkillBinding(skill, item?["revision_policy"]?.GetValue<string>() ?? "latest"));
            }
        }

        return result;
    }

    /// <summary>Read the canonical Agent direct-tool allowlist used to constrain Rule action references.</summary>
    public static IReadOnlyList<string> AllowedToolsOf(string canonicalDefinition)
    {
        var node = JsonNode.Parse(canonicalDefinition)?["allowed_tools"]?.AsArray();
        if (node is null)
        {
            return Array.Empty<string>();
        }

        return node
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    /// <summary>由 canonical definition 取出 pinned Agent-Runtime Workflow reference。</summary>
    public static AgentWorkflowRef WorkflowOf(string canonicalDefinition)
    {
        var node = JsonNode.Parse(canonicalDefinition)?["runtime_workflow"];
        return new AgentWorkflowRef(
            node?["id"]?.GetValue<string>(),
            node?["revision"]?.GetValue<int>() ?? 0);
    }

    /// <summary>
    /// 將 Workflow 驗證器回傳的 canonicalRuleSet 寫回完整 Agent definition。Backend 不解讀 AST，
    /// 只保存引擎正規化結果，讓後續 hash/publish/restore 都以同一份 canonical bytes 為準。
    /// </summary>
    public static string WithBusinessRules(string canonicalDefinition, JsonElement canonicalRuleSet)
    {
        var definition = JsonNode.Parse(canonicalDefinition)!.AsObject();
        definition["business_rules"] = CanonicalizeNode(ToNode(canonicalRuleSet))
                                       ?? JsonNode.Parse(AgentDefaults.EmptyBusinessRules);
        return CanonicalizeDefinition(definition.ToJsonString());
    }

    public static bool IsBusinessRuleOnlyCanonicalization(
        string lockedDefinition,
        string candidateDefinition)
    {
        try
        {
            using var candidate = JsonDocument.Parse(candidateDefinition);
            if (!candidate.RootElement.TryGetProperty(
                    "business_rules",
                    out var candidateRuleSet))
            {
                return false;
            }

            return string.Equals(
                WithBusinessRules(lockedDefinition, candidateRuleSet),
                candidateDefinition,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 對完整 definition 遞迴排序 object key。Dapper 從 jsonb::text 讀回時不保證保留 C# 的
    /// insertion order；所有會進 hash 的 bytes 都必須先經此處，才能和 in-memory provider 一致。
    /// Array 順序保留，因為 rules、bindings 等陣列順序具有語意。
    /// </summary>
    public static string CanonicalizeDefinition(string definition)
        => CanonicalizeNode(JsonNode.Parse(definition))!.ToJsonString();

    /// <summary>
    /// Read an authoritative persisted definition. JSONB is never a fallback: the exact
    /// canonical bytes and their digest must both be present, strict UTF-8 without a BOM,
    /// a definition-shaped JSON object, and already in the canonical representation.
    /// </summary>
    public static string ReadAuthoritativeDefinition(
        byte[]? canonicalBytes,
        string? definitionSha256,
        string authority)
    {
        if (canonicalBytes is null)
        {
            throw new InvalidOperationException(
                $"{authority} lacks authoritative canonical definition bytes");
        }
        if (canonicalBytes.Length > AgentExecutionContract.MaxCanonicalDefinitionBytes)
        {
            throw new InvalidOperationException(
                $"{authority} canonical definition exceeds its byte limit");
        }
        if (!SkillHash.MatchesSha256(canonicalBytes, definitionSha256))
        {
            throw new InvalidOperationException(
                $"{authority} canonical definition hash mismatch");
        }
        if (canonicalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidOperationException(
                $"{authority} canonical definition must not contain a UTF-8 BOM");
        }

        try
        {
            var definition = StrictUtf8.GetString(canonicalBytes);
            using var document = JsonDocument.Parse(canonicalBytes);
            if (!HasDefinitionShape(document.RootElement))
            {
                throw new InvalidOperationException(
                    $"{authority} canonical definition has an invalid root schema");
            }
            if (!string.Equals(
                    CanonicalizeDefinition(definition),
                    definition,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{authority} definition bytes are not canonical JSON");
            }
            return definition;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
        {
            throw new InvalidOperationException(
                $"{authority} canonical definition is not strict UTF-8 JSON",
                ex);
        }
    }

    /// <summary>
    /// Normalize legacy authoring role entries before validate/publish/restore creates a new
    /// lifecycle result. Existing immutable published revisions are not rewritten.
    /// </summary>
    public static string CanonicalizeForLifecycleWrite(string definition)
    {
        var canonical = JsonNode.Parse(definition)!.AsObject();
        if (canonical["audience"] is JsonArray audience)
        {
            canonical["audience"] = ToAudienceArray(
                audience
                    .Select(item => item?.GetValue<string>())
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray());
        }
        return CanonicalizeDefinition(canonical.ToJsonString());
    }

    /// <summary>
    /// Validate the persisted authoring definition against the immutable D3 execution-snapshot
    /// contract. The optional name is stored outside the definition but appears in the snapshot.
    /// </summary>
    public static IReadOnlyList<AgentValidationError> Validate(
        string canonicalDefinition,
        string? agentName = null)
    {
        var errors = new List<AgentValidationError>();
        var definitionBytes = Encoding.UTF8.GetByteCount(canonicalDefinition);
        if (definitionBytes > AgentExecutionContract.MaxCanonicalDefinitionBytes)
        {
            errors.Add(new AgentValidationError(
                "definition",
                $"canonical definition 不可超過 {AgentExecutionContract.MaxCanonicalDefinitionBytes} UTF-8 bytes"));
        }
        var def = JsonNode.Parse(canonicalDefinition)!.AsObject();

        if (agentName is not null)
        {
            if (string.IsNullOrWhiteSpace(agentName))
            {
                errors.Add(new AgentValidationError("name", "name 不可為空"));
            }
            else if (agentName.Length > AgentExecutionContract.MaxAgentNameLength)
            {
                errors.Add(new AgentValidationError(
                    "name",
                    $"name 不可超過 {AgentExecutionContract.MaxAgentNameLength} 字元"));
            }
        }

        var systemPrompt = def["system_prompt"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            errors.Add(new AgentValidationError("system_prompt", "system_prompt 不可為空"));
        }
        else if (systemPrompt.Length > AgentExecutionContract.MaxSystemPromptLength)
        {
            errors.Add(new AgentValidationError(
                "system_prompt",
                $"system_prompt 不可超過 {AgentExecutionContract.MaxSystemPromptLength} 字元"));
        }

        var roles = def["execution_roles"]!.AsArray();
        if (roles.Count == 0)
        {
            errors.Add(new AgentValidationError("execution_roles", "至少需要一個 execution role（worker 或 verifier）"));
        }
        else
        {
            foreach (var role in roles)
            {
                var value = role?.GetValue<string>();
                if (value is null || !ValidExecutionRoles.Contains(value))
                {
                    errors.Add(new AgentValidationError("execution_roles", $"不支援的 execution role：{value}"));
                }
            }
        }
        ValidateList(
            def, "execution_roles", AgentExecutionContract.MaxExecutionRoles, errors);
        ValidateList(def, "audience", AgentExecutionContract.MaxAudience, errors);
        if (def["audience"] is JsonArray audience)
        {
            foreach (var entry in audience)
            {
                var value = entry?.GetValue<string>();
                if (!AgentAudience.IsCanonicalEntry(value))
                {
                    errors.Add(new AgentValidationError(
                        "audience",
                        $"audience entry must be role:ADMIN, role:USER, or group:<canonical-id>: {value}"));
                }
            }
        }
        ValidateList(def, "allowed_tools", AgentExecutionContract.MaxAllowedTools, errors);
        ValidateList(
            def, "knowledge_sources", AgentExecutionContract.MaxKnowledgeSources, errors);

        if (def["output_contract"] is not JsonObject)
        {
            errors.Add(new AgentValidationError(
                "output_contract", "output_contract 必須是 JSON object"));
        }
        if (def["business_rules"] is not JsonObject)
        {
            errors.Add(new AgentValidationError(
                "business_rules", "business_rules 必須是 JSON object"));
        }

        var bindings = def["skill_bindings"]!.AsArray();
        if (bindings.Count > AgentExecutionContract.MaxSkillBindings)
        {
            errors.Add(new AgentValidationError(
                "skill_bindings",
                $"skill_bindings 最多 {AgentExecutionContract.MaxSkillBindings} 筆"));
        }

        var workflowId = def["runtime_workflow"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workflowId) || !Guid.TryParse(workflowId, out _))
        {
            errors.Add(new AgentValidationError("runtime_workflow", "runtime_workflow.id 必須是已發布 agent-runtime Workflow 的合法 id"));
        }
        if (def["runtime_workflow"]?["revision"]?.GetValue<int>() is not int workflowRevision
            || workflowRevision < 1)
        {
            errors.Add(new AgentValidationError(
                "runtime_workflow", "runtime_workflow.revision 必須大於等於 1"));
        }

        foreach (var binding in bindings)
        {
            var policy = binding?["revision_policy"]?.GetValue<string>();
            if (!string.Equals(policy, "latest", StringComparison.Ordinal))
            {
                errors.Add(new AgentValidationError(
                    "skill_bindings",
                    $"D1 只支援 revision_policy=latest，收到：{policy}"));
            }
        }

        foreach (var source in def["knowledge_sources"]!.AsArray())
        {
            var value = source?.GetValue<string>();
            if (value is null
                || !Guid.TryParseExact(value, "D", out var parsed)
                || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
            {
                errors.Add(new AgentValidationError(
                    "knowledge_sources",
                    $"knowledge_sources must contain canonical document UUIDs: {value}"));
            }
        }

        if (def["runtime_limits"] is not JsonObject limits)
        {
            errors.Add(new AgentValidationError(
                "runtime_limits", "runtime_limits 必須是 JSON object"));
        }
        else
        {
            ValidateLimit(
                limits, "max_tool_rounds", AgentExecutionContract.MaxToolRounds, errors);
            ValidateLimit(
                limits, "max_context_rounds", AgentExecutionContract.MaxContextRounds, errors);
            ValidateLimit(
                limits, "timeout_seconds", AgentExecutionContract.MaxTimeoutSeconds, errors);
            ValidateLimit(
                limits, "token_budget", AgentExecutionContract.MaxTokenBudget, errors);
            ValidateLimit(
                limits, "step_budget", AgentExecutionContract.MaxStepBudget, errors);
        }

        return errors;
    }

    private static void ValidateList(
        JsonObject definition,
        string field,
        int max,
        ICollection<AgentValidationError> errors)
    {
        if (definition[field] is not JsonArray values)
        {
            errors.Add(new AgentValidationError(field, $"{field} 必須是 JSON array"));
            return;
        }
        if (values.Count > max)
        {
            errors.Add(new AgentValidationError(field, $"{field} 最多 {max} 筆"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                errors.Add(new AgentValidationError(field, $"{field} 不可包含空白值"));
                continue;
            }
            if (!seen.Add(text))
            {
                errors.Add(new AgentValidationError(field, $"{field} 不可包含重複值"));
            }
        }
    }

    private static void ValidateLimit(
        JsonObject limits,
        string field,
        int max,
        ICollection<AgentValidationError> errors)
    {
        if (limits[field] is not JsonValue value
            || !value.TryGetValue<int>(out var number)
            || number is < 0
            || number > max)
        {
            errors.Add(new AgentValidationError(
                $"runtime_limits.{field}",
                $"runtime_limits.{field} 必須介於 0 與 {max}"));
        }
    }

    /// <summary>
    /// 這些欄位在領域上是 set 而非有序 list；trim、去重並排序，避免等價 authority
    /// 因 UI 順序不同得到不同 definition hash。
    /// </summary>
    private static JsonArray ToSetArray(IReadOnlyList<string>? values)
    {
        var array = new JsonArray();
        if (values is not null)
        {
            foreach (var v in values
                         .Where(v => !string.IsNullOrWhiteSpace(v))
                         .Select(v => v.Trim())
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(v => v, StringComparer.Ordinal))
            {
                array.Add(v);
            }
        }

        return array;
    }

    private static JsonArray ToAudienceArray(IReadOnlyList<string>? values)
        => ToSetArray(values is null
            ? null
            : values.Select(AgentAudience.NormalizeAuthoringEntry).ToArray());

    private static JsonArray ToKnowledgeSourceArray(IReadOnlyList<string>? values)
        => ToSetArray(values is null
            ? null
            : values
                .Where(value => value is not null)
                .Select(value => Guid.TryParse(value.Trim(), out var parsed)
                    ? parsed.ToString("D")
                    : value)
                .ToArray());

    private static JsonArray ToBindings(IReadOnlyList<AgentSkillBinding>? bindings)
    {
        var array = new JsonArray();
        if (bindings is null)
        {
            return array;
        }

        // 依 skill 名去重(保留首次出現的順序/policy):agent_revision_skill 的 PK 是 (agent_id, agent_revision,
        // skill_id) — 重複綁定在 Dapper 端會撞 PK,不去重則兩路徑分歧。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in bindings)
        {
            var skill = b.Skill?.Trim();
            if (!string.IsNullOrEmpty(skill) && seen.Add(skill))
            {
                var revisionPolicy = string.IsNullOrWhiteSpace(b.RevisionPolicy)
                    ? "latest"
                    : b.RevisionPolicy.Trim();
                array.Add(new JsonObject
                {
                    ["skill"] = skill,
                    ["revision_policy"] = revisionPolicy,
                });
            }
        }

        return array;
    }

    private static JsonObject ToLimits(AgentRuntimeLimits? limits)
    {
        var l = limits ?? new AgentRuntimeLimits();
        return new JsonObject
        {
            ["max_tool_rounds"] = l.MaxToolRounds,
            ["max_context_rounds"] = l.MaxContextRounds,
            ["timeout_seconds"] = l.TimeoutSeconds,
            ["token_budget"] = l.TokenBudget,
            ["step_budget"] = l.StepBudget,
        };
    }

    // runtime_workflow 缺席/空 → 預設 pin 到 P0 系統種子(Default Agent-Runtime Workflow current revision):
    // Agent Builder 不暴露 workflow id(A-UI-07),前端不帶此欄,種子本就是「發布時的預設 pin 目標」。
    private static JsonObject ToWorkflow(AgentWorkflowRef? workflow)
    {
        var id = string.IsNullOrWhiteSpace(workflow?.Id) ? AgentDefaults.RuntimeWorkflowId : workflow!.Id;
        var revision = workflow?.Revision is int r and > 0 ? r : AgentDefaults.RuntimeWorkflowRevision;
        return new JsonObject { ["id"] = id, ["revision"] = revision };
    }

    private static JsonNode? ToNode(JsonElement? element)
        => element is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } e
            ? JsonNode.Parse(e.GetRawText())
            : null;

    /// <summary>遞迴依 ordinal key 排序 JSON object；array 順序保留（JSON array 具語意）。</summary>
    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => new JsonObject(
                obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, CanonicalizeNode(p.Value)))),
            JsonArray arr => new JsonArray(arr.Select(CanonicalizeNode).ToArray()),
            _ => node.DeepClone(),
        };
    }

    private static bool HasDefinitionShape(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && HasKind(root, "system_prompt", JsonValueKind.String)
           && HasKind(root, "execution_roles", JsonValueKind.Array)
           && HasKind(root, "capabilities", JsonValueKind.Array)
           && HasKind(root, "output_contract", JsonValueKind.Object)
           && HasKind(root, "audience", JsonValueKind.Array)
           && HasKind(root, "allowed_tools", JsonValueKind.Array)
           && HasKind(root, "skill_bindings", JsonValueKind.Array)
           && HasKind(root, "knowledge_sources", JsonValueKind.Array)
           && HasKind(root, "business_rules", JsonValueKind.Object)
           && HasKind(root, "runtime_limits", JsonValueKind.Object)
           && HasKind(root, "runtime_workflow", JsonValueKind.Object);

    private static bool HasKind(
        JsonElement root,
        string property,
        JsonValueKind expected)
        => root.TryGetProperty(property, out var value)
           && value.ValueKind == expected;
}
