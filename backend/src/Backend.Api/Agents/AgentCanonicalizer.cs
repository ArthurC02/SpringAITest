using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Agents;

/// <summary>
/// Agent 定義的 canonicalize 與(D1 最小)驗證。所有集合欄位缺席/null/空 → 明確空陣列
/// (fail closed,禁止 null=unrestricted,02-spec §2.1/§7.2);business_rules 本期一律存 canonical
/// 空 AST(忽略輸入,D2 才引入 rule 編輯);key 以固定順序輸出 → canonical 文字可重現、可雜湊。
/// name/description/slug 是 agent 欄位而非定義內容,不入 canonical 定義。
/// </summary>
public static class AgentCanonicalizer
{
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
            ["audience"] = ToSetArray(req.Audience),
            ["allowed_tools"] = ToSetArray(req.AllowedTools),
            ["skill_bindings"] = ToBindings(req.SkillBindings),
            ["knowledge_sources"] = ToSetArray(req.KnowledgeSources),
            // 本期固定 canonical 空 AST(忽略 req.BusinessRules)。
            ["business_rules"] = JsonNode.Parse(AgentDefaults.EmptyBusinessRules),
            ["runtime_limits"] = ToLimits(req.RuntimeLimits),
            ["runtime_workflow"] = ToWorkflow(req.RuntimeWorkflow),
        };
        return obj.ToJsonString();
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

    /// <summary>由 canonical definition 取出 pinned Agent-Runtime Workflow reference。</summary>
    public static AgentWorkflowRef WorkflowOf(string canonicalDefinition)
    {
        var node = JsonNode.Parse(canonicalDefinition)?["runtime_workflow"];
        return new AgentWorkflowRef(
            node?["id"]?.GetValue<string>(),
            node?["revision"]?.GetValue<int>() ?? 0);
    }

    /// <summary>D1 最小驗證:system_prompt 非空、execution_roles 非空且皆屬 {worker,verifier}、runtime_workflow.id 為合法 uuid。</summary>
    public static IReadOnlyList<AgentValidationError> Validate(string canonicalDefinition)
    {
        var errors = new List<AgentValidationError>();
        var def = JsonNode.Parse(canonicalDefinition)!.AsObject();

        if (string.IsNullOrWhiteSpace(def["system_prompt"]?.GetValue<string>()))
        {
            errors.Add(new AgentValidationError("system_prompt", "system_prompt 不可為空"));
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

        var workflowId = def["runtime_workflow"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(workflowId) || !Guid.TryParse(workflowId, out _))
        {
            errors.Add(new AgentValidationError("runtime_workflow", "runtime_workflow.id 必須是已發布 agent-runtime Workflow 的合法 id"));
        }

        foreach (var binding in def["skill_bindings"]!.AsArray())
        {
            var policy = binding?["revision_policy"]?.GetValue<string>();
            if (!string.Equals(policy, "latest", StringComparison.Ordinal))
            {
                errors.Add(new AgentValidationError(
                    "skill_bindings",
                    $"D1 只支援 revision_policy=latest，收到：{policy}"));
            }
        }

        return errors;
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
}
