using Microsoft.Extensions.Primitives;
using Platform.Service.Exceptions;

namespace Platform.Web.Infrastructure;

/// <summary>
/// Idempotency-Key header 解析中兩個呼叫端(<see cref="HttpChatIdentityAccessor"/> 的 D3/D5/D7 命令與
/// <see cref="Controllers.DocumentController"/> 的文件受理)真正共用的那一半:header 名、「恰一個值」
/// 前置檢查,以及兩段對外錯誤文案 —— 先前是兩份各自維護的複製常數,漂移了就變成兩種對外訊息。
///
/// 刻意「不」收進來的:長度上限(chat 512 / documents 128)、字元集(documents 另限可列印 ASCII)、
/// 是否 Trim、缺值行為(chat 回 null / documents 生 GUID)。這些每個站點都不同,留在呼叫端明示,
/// 收進來只會變成一堆旗標。
/// </summary>
internal static class IdempotencyKeyHeader
{
    internal const string Name = "Idempotency-Key";
    internal const string InvalidMessage = "Idempotency-Key is invalid";

    /// <summary>
    /// 零個值 → <c>null</c>(缺值行為由呼叫端決定);多個值 → 400;恰一個值 → 原樣回傳,不 Trim
    /// (null 元素正規化成空字串,交給呼叫端自己的非空檢查拒絕 —— 與兩處原本的行為逐位元相同)。
    /// </summary>
    internal static string? SingleValueOrNull(StringValues values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new WorkflowBadInputException(Name + " must contain exactly one value");
        }

        return values[0] ?? string.Empty;
    }
}
