using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Service.Dtos;

/// <summary>執行工作流的請求 body:{ "input": {任意 map} }。input 不可為 null。</summary>
public sealed record WorkflowInvokeRequest(
    [Required(ErrorMessage = "input 不可為空")]
    Dictionary<string, JsonElement>? Input);

/// <summary>執行工作流的回應:{ "workflow", "output" };output 是任意 map。</summary>
public sealed record WorkflowInvokeResponse(
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("output")] Dictionary<string, JsonElement> Output);

/// <summary>工作流清單項目。JSON key 是 snake_case 的 required_role(前端與下游皆用此形式)。
/// input_schema 是引擎回傳的動態欄位,代理層原樣穿透 — 這裡若不宣告,重序列化會把它靜默吃掉
/// (前端據它動態渲染執行表單);同理未來引擎新增的欄位也應比照補上或改走 JsonElement 穿透。</summary>
public sealed record WorkflowInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("input_schema")] JsonElement? InputSchema = null);
