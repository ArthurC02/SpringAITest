using System.Text.Json.Nodes;

namespace Backend.Api.Agents;

/// <summary>
/// D1 系統級常數。Default Agent-Runtime Workflow 是 system-owned、published、不可編輯的 revision;
/// 其固定 id/current revision 於 DbBootstrap 種入,亦供 InMemory 對偶與 Agent 驗證引用(兩路徑同一事實)。
/// rev1 是早期缺少明確 Start/End 的歷史 fixture；bootstrap 絕不原地改寫它。修正版從 rev2 開始，
/// 新 Agent 預設 pin rev2，既有 rev1 pin 則維持原快照以保留 audit/replay 語意。
/// definition 是 canonical Graph IR JSON,形狀依 02-spec §7.3 的 visible-required/optional stages
/// (Graph IR = 是 的階段;wrapper-owned 的 Identity/Budget/Final Checkpoint/Cleanup/Audit 不入 Graph)。
/// 內容 D3 才會被編譯/消費,此期只求形狀正確。
/// </summary>
public static class AgentDefaults
{
    /// <summary>system-owned workflow 的租戶哨符(非任何真實租戶 code)。</summary>
    public const string SystemTenant = "__system__";

    /// <summary>Default Agent-Runtime Workflow 的固定 id(種子與 InMemory 對偶皆用此值)。</summary>
    public const string RuntimeWorkflowId = "00000000-0000-4000-8000-000000000001";

    public const int LegacyRuntimeWorkflowRevision = 1;

    public const int RuntimeWorkflowRevision = 2;

    public const string RuntimeWorkflowName = "Default Agent-Runtime Workflow";

    public const string RuntimeWorkflowKind = "agent-runtime";

    /// <summary>
    /// canonical Agent-Runtime Graph IR。visible-required stages 皆在;Bounded Agent Loop 內含
    /// Model Step / (optional) Load Skill / Tool Gate / Tool Call / Checkpoint+Budget Gate;
    /// 迴圈與 repair 皆有上限(maxIterations / maxRepairRounds)。edges 線性串接、單一 Start/End。
    /// </summary>
    public const string RuntimeWorkflowDefinition = """
        {"schemaVersion":1,"kind":"agent-runtime","nodes":[{"id":"start","type":"start","typeVersion":"1.0","config":{}},{"id":"preflight","type":"dependency_and_capability_preflight","typeVersion":"1.0","config":{}},{"id":"inject_context","type":"inject_authorized_context","typeVersion":"1.0","config":{}},{"id":"initial_checkpoint","type":"checkpoint","typeVersion":"1.0","config":{}},{"id":"agent_loop","type":"bounded_agent_loop","typeVersion":"1.0","config":{"maxIterations":8},"children":[{"id":"model_step","type":"model_step","typeVersion":"1.0","config":{}},{"id":"load_skill","type":"load_skill","typeVersion":"1.0","config":{},"optional":true},{"id":"tool_gate","type":"tool_policy_and_approval_gate","typeVersion":"1.0","config":{}},{"id":"tool_call","type":"tool_call_and_observation","typeVersion":"1.0","config":{}},{"id":"checkpoint_budget","type":"checkpoint_and_budget_gate","typeVersion":"1.0","config":{}}]},{"id":"validate_output","type":"validate_structured_output","typeVersion":"1.0","config":{}},{"id":"repair","type":"bounded_repair_or_controlled_failure","typeVersion":"1.0","config":{"maxRepairRounds":2}},{"id":"end","type":"end","typeVersion":"1.0","config":{}}],"edges":[{"id":"e0","source":{"nodeId":"start","port":"out"},"target":{"nodeId":"preflight","port":"in"}},{"id":"e1","source":{"nodeId":"preflight","port":"out"},"target":{"nodeId":"inject_context","port":"in"}},{"id":"e2","source":{"nodeId":"inject_context","port":"out"},"target":{"nodeId":"initial_checkpoint","port":"in"}},{"id":"e3","source":{"nodeId":"initial_checkpoint","port":"out"},"target":{"nodeId":"agent_loop","port":"in"}},{"id":"e4","source":{"nodeId":"agent_loop","port":"out"},"target":{"nodeId":"validate_output","port":"in"}},{"id":"e5","source":{"nodeId":"validate_output","port":"out"},"target":{"nodeId":"repair","port":"in"}},{"id":"e6","source":{"nodeId":"repair","port":"out"},"target":{"nodeId":"end","port":"in"}}],"governance":{"maxSteps":40,"maxConcurrency":1}}
        """;

    /// <summary>business_rules 缺席/null 時使用的 canonical 空 Rule AST。</summary>
    public const string EmptyBusinessRules = """{"version":1,"rules":[]}""";

    /// <summary>
    /// Backend seed 的最小結構守衛。完整 node schema/port 語意仍由 Workflow compiler 擁有；
    /// 此處只防止把沒有唯一 start/end、不可達或無法抵達 terminal 的壞 fixture 標成 published。
    /// </summary>
    public static IReadOnlyList<string> ValidateRuntimeWorkflowFixture()
    {
        var errors = new List<string>();
        var root = JsonNode.Parse(RuntimeWorkflowDefinition)?.AsObject();
        if (root is null || root["kind"]?.GetValue<string>() != RuntimeWorkflowKind)
        {
            return new[] { "kind 必須是 agent-runtime" };
        }

        var nodes = root["nodes"]?.AsArray();
        var edges = root["edges"]?.AsArray();
        if (nodes is null || edges is null)
        {
            return new[] { "nodes/edges 不可缺少" };
        }

        var nodeEntries = nodes
            .Select(n => (
                Id: n?["id"]?.GetValue<string>() ?? "",
                Type: n?["type"]?.GetValue<string>() ?? "",
                Node: n))
            .ToList();
        var duplicateIds = nodeEntries
            .Where(n => n.Id.Length > 0)
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (nodeEntries.Any(n => n.Id.Length == 0) || duplicateIds.Count > 0)
        {
            errors.Add("node id 必須非空且唯一");
        }

        var starts = nodeEntries.Where(n => n.Type == "start").Select(n => n.Id).ToList();
        var ends = nodeEntries.Where(n => n.Type == "end").Select(n => n.Id).ToList();
        if (starts.Count != 1)
        {
            errors.Add("必須恰有一個 start");
        }
        if (ends.Count != 1)
        {
            errors.Add("必須恰有一個 end");
        }

        var requiredTypes = new[]
        {
            "dependency_and_capability_preflight",
            "inject_authorized_context",
            "checkpoint",
            "bounded_agent_loop",
            "validate_structured_output",
            "bounded_repair_or_controlled_failure",
        };
        foreach (var type in requiredTypes.Where(t => nodeEntries.All(n => n.Type != t)))
        {
            errors.Add($"缺少必要 stage：{type}");
        }

        var loop = nodeEntries.FirstOrDefault(n => n.Type == "bounded_agent_loop").Node;
        if (loop?["config"]?["maxIterations"]?.GetValue<int>() is not > 0)
        {
            errors.Add("bounded_agent_loop.maxIterations 必須大於 0");
        }
        var requiredChildren = new[]
        {
            "model_step",
            "tool_policy_and_approval_gate",
            "tool_call_and_observation",
            "checkpoint_and_budget_gate",
        };
        var children = loop?["children"]?.AsArray();
        foreach (var type in requiredChildren.Where(t =>
                     children is null || children.All(c => c?["type"]?.GetValue<string>() != t)))
        {
            errors.Add($"agent loop 缺少必要 stage：{type}");
        }

        var repair = nodeEntries.FirstOrDefault(
            n => n.Type == "bounded_repair_or_controlled_failure").Node;
        if (repair?["config"]?["maxRepairRounds"]?.GetValue<int>() is not > 0)
        {
            errors.Add("repair.maxRepairRounds 必須大於 0");
        }

        if (starts.Count == 1 && ends.Count == 1 && duplicateIds.Count == 0)
        {
            var ids = nodeEntries.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var forward = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            var reverse = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                var source = edge?["source"]?["nodeId"]?.GetValue<string>() ?? "";
                var target = edge?["target"]?["nodeId"]?.GetValue<string>() ?? "";
                if (!ids.Contains(source) || !ids.Contains(target))
                {
                    errors.Add($"edge 引用未知 node：{source}->{target}");
                    continue;
                }
                forward[source].Add(target);
                reverse[target].Add(source);
            }

            var reachable = Traverse(starts[0], forward);
            if (!ids.SetEquals(reachable))
            {
                errors.Add("所有 node 必須可由 start 到達");
            }
            var canReachEnd = Traverse(ends[0], reverse);
            if (!ids.SetEquals(canReachEnd))
            {
                errors.Add("所有 node 必須可抵達 end");
            }
        }

        return errors;
    }

    private static HashSet<string> Traverse(
        string root,
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current) && seen.Add(current))
        {
            foreach (var next in adjacency[current])
            {
                pending.Push(next);
            }
        }
        return seen;
    }
}
