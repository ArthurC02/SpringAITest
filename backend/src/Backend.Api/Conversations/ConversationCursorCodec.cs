using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Backend.Api.Common;
using Microsoft.AspNetCore.WebUtilities;

namespace Backend.Api.Conversations;

/// <summary>Strict fixed-size, versioned keyset cursor bound to tenant and user.</summary>
public sealed class ConversationCursorCodec
{
    private const byte Version = 1;
    private const int DataBytes = 1 + sizeof(long) + sizeof(long);
    private const int TagBytes = 32;
    private const int PayloadBytes = DataBytes + TagBytes;
    private const string InvalidMessage = "聊天歷史游標無效";
    private static readonly byte[] KeyDomain = Encoding.UTF8.GetBytes(
        "springaitest.backend.conversation-cursor.key.v1");
    private static readonly byte[] SigningDomain = Encoding.UTF8.GetBytes(
        "springaitest.backend.conversation-cursor.payload.v1");
    private readonly byte[] _key;

    public ConversationCursorCodec(string internalToken)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(internalToken);
        try
        {
            _key = HMACSHA256.HashData(tokenBytes, KeyDomain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    public string Encode(ConversationItem item, string tenantId, string userId)
    {
        Span<byte> payload = stackalloc byte[PayloadBytes];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..], Utc(item.CreatedAt).Ticks);
        BinaryPrimitives.WriteInt64BigEndian(payload[(1 + sizeof(long))..], item.Id);
        Sign(payload[..DataBytes], tenantId, userId).CopyTo(payload[DataBytes..]);
        return WebEncoders.Base64UrlEncode(payload);
    }

    public ConversationPosition Decode(string encoded, string tenantId, string userId)
    {
        if (encoded.Length != WebEncoders.Base64UrlEncode(new byte[PayloadBytes]).Length
            || !encoded.All(IsBase64UrlCharacter))
        {
            throw Invalid();
        }

        byte[] payload;
        try
        {
            payload = WebEncoders.Base64UrlDecode(encoded);
        }
        catch (FormatException)
        {
            throw Invalid();
        }

        if (payload.Length != PayloadBytes
            || !string.Equals(WebEncoders.Base64UrlEncode(payload), encoded, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var expectedTag = Sign(payload.AsSpan(0, DataBytes), tenantId, userId);
        if (!CryptographicOperations.FixedTimeEquals(payload.AsSpan(DataBytes), expectedTag))
        {
            throw Invalid();
        }

        if (payload[0] != Version)
        {
            throw Invalid();
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1));
        var id = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1 + sizeof(long)));
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || id <= 0)
        {
            throw Invalid();
        }

        return new ConversationPosition(new DateTime(ticks, DateTimeKind.Utc), id);
    }

    private byte[] Sign(ReadOnlySpan<byte> data, string tenantId, string userId)
    {
        var tenantBytes = Encoding.UTF8.GetBytes(tenantId);
        var userBytes = Encoding.UTF8.GetBytes(userId);
        var input = new byte[
            SigningDomain.Length + data.Length
            + sizeof(int) + tenantBytes.Length
            + sizeof(int) + userBytes.Length];
        var offset = 0;
        SigningDomain.CopyTo(input, offset);
        offset += SigningDomain.Length;
        data.CopyTo(input.AsSpan(offset));
        offset += data.Length;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset), tenantBytes.Length);
        offset += sizeof(int);
        tenantBytes.CopyTo(input, offset);
        offset += tenantBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset), userBytes.Length);
        offset += sizeof(int);
        userBytes.CopyTo(input, offset);
        return HMACSHA256.HashData(_key, input);
    }

    private static bool IsBase64UrlCharacter(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_';

    private static DateTime Utc(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static ApiException Invalid()
        => new(StatusCodes.Status400BadRequest, InvalidMessage);
}
