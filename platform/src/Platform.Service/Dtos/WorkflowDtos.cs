using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Platform.Service.Dtos;

/// <summary>執行 Skill 的請求 body:{ "input": {任意 map} }。input 不可為 null。
/// 名稱沿用自已退役的 /api/workflows/{name} 代理端點;現由 POST /api/skills/{name}/invoke 沿用同一形狀。</summary>
public sealed record WorkflowInvokeRequest(
    [Required(ErrorMessage = "input 不可為空")]
    Dictionary<string, JsonElement>? Input);
