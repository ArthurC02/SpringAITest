using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.OrchestratorRuns;

internal readonly record struct OrchestratorSnapshotPin(
    Guid WorkflowId,
    int WorkflowRevision,
    string Hash,
    int TokenCap);

internal static class OrchestratorRunSnapshotProjection
{
    public static JsonElement Budgets(JsonElement snapshot)
    {
        var limits = snapshot.GetProperty("limits");
        var value = new JsonObject
        {
            ["maxContextRounds"] = limits.GetProperty("max_context_rounds").GetInt32(),
            ["maxTasks"] = limits.GetProperty("max_tasks").GetInt32(),
            ["maxChildRuns"] = limits.GetProperty("max_child_runs").GetInt32(),
            ["maxConcurrency"] = limits.GetProperty("max_concurrency").GetInt32(),
            ["maxRepairRounds"] = limits.GetProperty("max_repair_rounds").GetInt32(),
            ["timeoutSeconds"] = limits.GetProperty("timeout_seconds").GetDouble(),
            ["tokenBudget"] = snapshot.GetProperty("token_budget").GetInt32(),
        };
        return JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
    }

    public static OrchestratorSnapshotPin? FindPin(
        JsonElement snapshot,
        string kind,
        Guid agentId,
        int revision)
    {
        IEnumerable<JsonElement> pins = kind == "verifier"
            ? [snapshot.GetProperty("verifier")]
            : snapshot.GetProperty("workers").EnumerateArray();

        foreach (var pin in pins)
        {
            if (Guid.TryParse(pin.GetProperty("agent_id").GetString(), out var id)
                && id == agentId
                && pin.GetProperty("agent_revision").GetInt32() == revision
                && Guid.TryParse(pin.GetProperty("workflow_id").GetString(), out var workflowId)
                && pin.TryGetProperty("token_cap", out var cap)
                && cap.TryGetInt32(out var tokenCap)
                && tokenCap > 0)
            {
                return new(
                    workflowId,
                    pin.GetProperty("workflow_revision").GetInt32(),
                    pin.GetProperty("snapshot_hash").GetString()!,
                    tokenCap);
            }
        }

        return null;
    }
}
