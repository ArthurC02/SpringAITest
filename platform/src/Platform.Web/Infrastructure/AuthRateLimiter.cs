using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Web.Infrastructure;

/// <summary>
/// Local-instance fixed-window limiter for public authentication endpoints.
/// Fixed-size open-addressed tables put a hard bound on state without making colliding identities share counters.
/// </summary>
public sealed class AuthRateLimiter
{
    internal const int ClientIpPermitLimit = 20;
    internal const int LoginAccountPermitLimit = 5;
    internal const int RegisterAccountPermitLimit = 3;
    internal const int MaximumIdentifierLength = 128;
    private const int PartitionCount = 8192;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly PartitionEntry?[] _clientIpPartitions = new PartitionEntry[PartitionCount];
    private readonly PartitionEntry?[] _accountPartitions = new PartitionEntry[PartitionCount];
    private readonly bool _enabled;
    private readonly TimeProvider _timeProvider;
    private readonly IAuthPartitionHasher _hasher;

    internal AuthRateLimiter(
        bool enabled,
        TimeProvider? timeProvider = null,
        IAuthPartitionHasher? hasher = null)
    {
        _enabled = enabled;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hasher = hasher ?? new KeyedAuthPartitionHasher();
    }

    internal bool TryAcquireClientIp(IPAddress? clientIp)
        => TryAcquire(
            _clientIpPartitions,
            _hasher.Hash("client-ip", ClientIpKey(clientIp), PartitionCount),
            ClientIpPermitLimit);

    internal bool TryAcquireLoginAccount(string? username)
        => TryAcquire(
            _accountPartitions,
            _hasher.Hash("login-account", Normalize(username), PartitionCount),
            LoginAccountPermitLimit);

    internal bool TryAcquireRegisterAccount(string? tenantCode, string? username)
    {
        var tenant = Normalize(tenantCode);
        var account = Normalize(username);
        return TryAcquire(
            _accountPartitions,
            _hasher.Hash(
                "register-account",
                $"{tenant.Length}:{tenant}{account}",
                PartitionCount),
            RegisterAccountPermitLimit);
    }

    private bool TryAcquire(PartitionEntry?[] partitions, AuthPartitionHash hash, int permitLimit)
    {
        if (!_enabled)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            int? reusableIndex = null;
            for (var offset = 0; offset < partitions.Length; offset++)
            {
                var index = (hash.InitialSlot + offset) % partitions.Length;
                var entry = partitions[index];
                if (entry is null)
                {
                    partitions[reusableIndex ?? index] = new PartitionEntry(hash.TagHigh, hash.TagLow, now, 1);
                    return true;
                }

                if (entry.TagHigh != hash.TagHigh || entry.TagLow != hash.TagLow)
                {
                    if (reusableIndex is null && now - entry.WindowStartedAt >= Window)
                    {
                        reusableIndex = index;
                    }
                    continue;
                }

                if (now - entry.WindowStartedAt >= Window)
                {
                    entry.WindowStartedAt = now;
                    entry.Count = 1;
                    return true;
                }

                if (entry.Count >= permitLimit)
                {
                    return false;
                }

                entry.Count++;
                return true;
            }

            if (reusableIndex is not null)
            {
                partitions[reusableIndex.Value] = new PartitionEntry(hash.TagHigh, hash.TagLow, now, 1);
                return true;
            }

            // The fixed table contains only active entries. Fail closed instead of evicting a victim partition.
            return false;
        }
    }

    private static string ClientIpKey(IPAddress? clientIp)
        => clientIp?.MapToIPv6().ToString() ?? "unknown";

    private static string Normalize(string? value)
    {
        var bounded = value is { Length: > MaximumIdentifierLength }
            ? value[..MaximumIdentifierLength]
            : value ?? string.Empty;
        return bounded.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }

    private sealed class PartitionEntry(
        ulong tagHigh,
        ulong tagLow,
        DateTimeOffset windowStartedAt,
        int count)
    {
        public ulong TagHigh { get; } = tagHigh;
        public ulong TagLow { get; } = tagLow;
        public DateTimeOffset WindowStartedAt { get; set; } = windowStartedAt;
        public int Count { get; set; } = count;
    }
}

internal readonly record struct AuthPartitionHash(int InitialSlot, ulong TagHigh, ulong TagLow);

internal interface IAuthPartitionHasher
{
    AuthPartitionHash Hash(string domain, string value, int partitionCount);
}

internal sealed class KeyedAuthPartitionHasher : IAuthPartitionHasher
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public AuthPartitionHash Hash(string domain, string value, int partitionCount)
    {
        var input = Encoding.UTF8.GetBytes(domain + '\0' + value);
        var digest = HMACSHA256.HashData(_key, input);
        return new AuthPartitionHash(
            (int)(BitConverter.ToUInt32(digest, 0) % partitionCount),
            BitConverter.ToUInt64(digest, 4),
            BitConverter.ToUInt64(digest, 12));
    }
}
