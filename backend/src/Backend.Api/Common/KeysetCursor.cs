using System.Text;

namespace Backend.Api.Common;

/// <summary>
/// Opaque keyset cursor for the cross-row lists paginated by (timestamp DESC, id DESC): O2's
/// unified runs list, O3's approval queue and O5's trigger fire ledger. One codec so the three
/// cannot drift into three subtly different encodings or rejection messages.
///
/// ponytail: not HMAC-signed (unlike <see cref="Conversations.ConversationCursorCodec"/>) because
/// every caller re-applies its own tenant/owner/role predicate server-side regardless of the
/// decoded position — a forged cursor can only shift which page comes back, never which rows are
/// authorized. Upgrade path if that ever stops being true: sign it the same way
/// ConversationCursorCodec does.
/// </summary>
public static class KeysetCursor
{
    public static string Encode(DateTime timestamp, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).Ticks}|{id:N}"));

    /// <summary>
    /// Decodes into the caller's own position record, or throws <see cref="ApiException"/> 400.
    /// Every malformed shape (bad base64, wrong field count, unparsable ticks/guid, out-of-range
    /// ticks) gets the same fixed message — a cursor is opaque, so its failure mode is too.
    /// </summary>
    public static T Decode<T>(string cursor, Func<DateTime, Guid, T> create)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && Guid.TryParseExact(parts[1], "N", out var id)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            {
                return create(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
            // falls through to the shared rejection below.
        }
        throw new ApiException(400, "cursor 無效");
    }

    /// <summary>
    /// True when the row sorts strictly after the cursor position in this codec's (timestamp DESC,
    /// id DESC) page order — the exclusive keyset predicate the SQL side writes as
    /// <c>(created_at, id) &lt; (@posCreatedAt, @posId)</c>. The in-memory repositories share it so
    /// the ordering rule is defined once for all three paginated lists.
    /// </summary>
    public static bool FollowsPosition(DateTime timestamp, Guid id, DateTime positionTimestamp, Guid positionId)
    {
        var byTime = DateTime.Compare(timestamp, positionTimestamp);
        return byTime != 0 ? byTime < 0 : id.CompareTo(positionId) < 0;
    }
}
