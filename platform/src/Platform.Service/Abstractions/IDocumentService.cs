using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>文件服務:代理下游 Python。轉發 4 個 X-* header 並轉譯下游狀態碼。</summary>
public interface IDocumentService
{
    /// <summary>受理文件(生成 id、發佈到佇列非同步處理,回 id/title/status=processing)。</summary>
    Task<DocumentAccepted> CreateAsync(DocumentCreateRequest request, UserContext ctx, CancellationToken ct = default);

    /// <summary>列出文件 —— 原樣穿透 backend JSON(snake_case),不套 DTO 以免吞掉 backend 新增欄位。</summary>
    Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>刪除文件;下游 404 → DocumentNotFound。</summary>
    Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default);
}
