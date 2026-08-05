using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>文件服務:代理 backend。轉發內部身分 headers 並轉譯下游狀態碼。</summary>
public interface IDocumentService
{
    /// <summary>先向 backend 配置穩定 id，再發佈到佇列非同步處理；同一 idempotency key 可安全重試。</summary>
    Task<DocumentAccepted> CreateAsync(
        DocumentCreateRequest request,
        UserContext ctx,
        string idempotencyKey,
        CancellationToken ct = default);

    /// <summary>列出文件 —— 原樣穿透 backend JSON(snake_case),不套 DTO 以免吞掉 backend 新增欄位。</summary>
    Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>刪除文件;下游 404 → DocumentNotFound。</summary>
    Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default);
}
