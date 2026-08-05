using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class AuthRateLimiterTests
{
    [Fact]
    public void CollidingInitialSlots_KeepUnrelatedAccountsIndependent()
    {
        var limiter = new AuthRateLimiter(enabled: true, hasher: new SameSlotHasher());

        for (var i = 0; i < AuthRateLimiter.LoginAccountPermitLimit; i++)
        {
            Assert.True(limiter.TryAcquireLoginAccount("victim"));
        }

        Assert.False(limiter.TryAcquireLoginAccount("victim"));
        Assert.True(limiter.TryAcquireLoginAccount("unrelated"));
        Assert.True(limiter.TryAcquireRegisterAccount("tenant", "victim"));
    }

    private sealed class SameSlotHasher : IAuthPartitionHasher
    {
        private readonly Dictionary<(string Domain, string Value), ulong> _tags = [];

        public AuthPartitionHash Hash(string domain, string value, int partitionCount)
        {
            var key = (domain, value);
            if (!_tags.TryGetValue(key, out var tag))
            {
                tag = (ulong)_tags.Count + 1;
                _tags.Add(key, tag);
            }

            return new AuthPartitionHash(InitialSlot: 7, TagHigh: tag, TagLow: tag);
        }
    }
}
