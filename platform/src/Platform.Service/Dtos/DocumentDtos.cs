using System.ComponentModel.DataAnnotations;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>建立文件的請求 body:{ "title", "text" }。</summary>
public sealed record DocumentCreateRequest(
    [NotBlank(ErrorMessage = "title 不可為空")]
    [StringLength(500, ErrorMessage = "title 長度不可超過 500 字")]
    string? Title,

    [NotBlank(ErrorMessage = "text 不可為空")]
    [StringLength(1_000_000, ErrorMessage = "text 長度不可超過 1000000 字")]
    string? Text);

/// <summary>建立文件已受理的回應(非同步處理)。JSON:{ id, title, status }(camelCase)。</summary>
public sealed record DocumentAccepted(string Id, string Title, string Status);

// 文件清單(GET /api/documents)改為原樣穿透 backend JSON(snake_case),不再套 DocumentInfo DTO
// (見 DocumentService.ListAsync / IDocumentService)——避免 backend 新增欄位被靜默吃掉。

/// <summary>
/// 發佈到 RabbitMQ 的文件處理訊息。JSON camelCase:
/// { documentId, tenantId, userId, title, text, correlationId }(與 backend 消費者共用契約)。
/// <c>correlationId</c> 由 <see cref="RabbitDocumentQueue"/> 在發佈當下填入(nullable:滾動部署期間
/// 佇列裡會有舊版沒有這個欄位的訊息,消費端必須照常處理)。
/// </summary>
public sealed record DocumentMessage(
    string DocumentId, string TenantId, string UserId, string Title, string Text,
    string? CorrelationId = null);
