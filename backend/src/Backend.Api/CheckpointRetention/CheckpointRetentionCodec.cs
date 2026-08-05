using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Backend.Api.Common;
using Microsoft.AspNetCore.WebUtilities;

namespace Backend.Api.CheckpointRetention;

public sealed class CheckpointRetentionCodec
{
    private const byte Version = 1;
    private const int CursorDataBytes = 1 + 1 + sizeof(long) + 16;
    private const int CandidateDataBytes = 1 + 1 + 16;
    private const int TagBytes = 32;
    private readonly byte[] _key;

    public CheckpointRetentionCodec(string internalToken)
    {
        _key = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(internalToken),
            Encoding.UTF8.GetBytes("springaitest.backend.checkpoint-retention.key.v1"));
    }

    public string EncodeCursor(CheckpointRetentionRow row)
    {
        Span<byte> payload = stackalloc byte[CursorDataBytes + TagBytes];
        payload[0] = Version;
        payload[1] = Kind(row.Kind);
        BinaryPrimitives.WriteInt64BigEndian(payload[2..], row.CompletedAt.ToUniversalTime().Ticks);
        row.RunId.TryWriteBytes(payload[(2 + sizeof(long))..], bigEndian: true, out _);
        Sign(payload[..CursorDataBytes]).CopyTo(payload[CursorDataBytes..]);
        return WebEncoders.Base64UrlEncode(payload);
    }

    public CheckpointRetentionPosition? DecodeCursor(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }
        var payload = Decode(encoded, CursorDataBytes);
        var kind = payload[1];
        if (kind is not 1 and not 2)
        {
            throw Invalid();
        }
        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(2));
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw Invalid();
        }
        var runId = new Guid(payload.AsSpan(2 + sizeof(long), 16), bigEndian: true);
        return new(new DateTime(ticks, DateTimeKind.Utc), runId, kind);
    }

    public string EncodeCandidate(string kind, Guid runId)
    {
        Span<byte> payload = stackalloc byte[CandidateDataBytes + TagBytes];
        payload[0] = Version;
        payload[1] = Kind(kind);
        runId.TryWriteBytes(payload[2..], bigEndian: true, out _);
        Sign(payload[..CandidateDataBytes]).CopyTo(payload[CandidateDataBytes..]);
        return WebEncoders.Base64UrlEncode(payload);
    }

    public (string Kind, Guid RunId) DecodeCandidate(string encoded)
    {
        var payload = Decode(encoded, CandidateDataBytes);
        var kind = payload[1] switch
        {
            1 => "agent_thread",
            2 => "root_context",
            _ => throw Invalid(),
        };
        return (kind, new Guid(payload.AsSpan(2, 16), bigEndian: true));
    }

    private byte[] Decode(string encoded, int dataBytes)
    {
        var expectedLength = WebEncoders.Base64UrlEncode(new byte[dataBytes + TagBytes]).Length;
        if (encoded.Length != expectedLength || !encoded.All(IsBase64Url))
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
        if (payload.Length != dataBytes + TagBytes
            || payload[0] != Version
            || !string.Equals(WebEncoders.Base64UrlEncode(payload), encoded, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                payload.AsSpan(dataBytes), Sign(payload.AsSpan(0, dataBytes))))
        {
            throw Invalid();
        }
        return payload;
    }

    private byte[] Sign(ReadOnlySpan<byte> data) => HMACSHA256.HashData(_key, data);

    private static byte Kind(string value) => value switch
    {
        "agent_thread" => 1,
        "root_context" => 2,
        _ => throw new InvalidOperationException("unknown checkpoint retention kind"),
    };

    private static bool IsBase64Url(char value) => value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static ApiException Invalid()
        => new(StatusCodes.Status400BadRequest, "checkpoint retention cursor is invalid");
}
