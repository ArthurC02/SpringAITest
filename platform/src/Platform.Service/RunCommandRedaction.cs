using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 耐久命令身分(command_id)是內部執行憑據,絕不進公開回應 —— D3(直接 Agent 執行)與 D5/D6
/// (Root Orchestrator)共用同一份事實。fail-closed:body 不是 JSON 物件就拋
/// <see cref="WorkflowInvocationException"/>(對外 502),不把一個「無法確認已剝除」的 body 回給呼叫端。
/// </summary>
internal static class RunCommandRedaction
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string StripCommandId(string json, string failurePrefix)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject run)
            {
                throw new JsonException("Backend run response is not an object");
            }

            run.Remove("command_id");
            return run.ToJsonString(Json);
        }
        catch (JsonException ex)
        {
            throw new WorkflowInvocationException(
                failurePrefix + "Backend run response is invalid",
                ex);
        }
    }
}
