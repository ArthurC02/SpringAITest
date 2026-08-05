using Backend.Api.Auth;
using Backend.Api.Common;
using Microsoft.AspNetCore.Http;

namespace Backend.Api.Tests;

public sealed class AuthServiceTimingTests
{
    [Fact]
    public async Task Login_MissingUser_VerifiesPrecomputedDummyHash_AndReturnsGeneric401()
    {
        var verifier = new RecordingPasswordVerifier(result: false);
        var service = new AuthService(new MissingUserRepository(), verifier);

        var exception = await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync(
            new LoginRequest("ghost", "submitted-password"),
            CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.Status);
        Assert.Equal("帳號或密碼錯誤", exception.Message);
        var call = Assert.Single(verifier.Calls);
        Assert.Equal("submitted-password", call.Password);
        Assert.Equal(AuthService.DummyPasswordHash, call.PasswordHash);
        Assert.StartsWith("$2a$11$", call.PasswordHash, StringComparison.Ordinal);
    }

    private sealed class RecordingPasswordVerifier(bool result) : IPasswordVerifier
    {
        public List<(string Password, string PasswordHash)> Calls { get; } = [];

        public bool Verify(string password, string passwordHash)
        {
            Calls.Add((password, passwordHash));
            return result;
        }
    }

    private sealed class MissingUserRepository : IAuthRepository
    {
        public Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct)
            => Task.FromResult<TenantRow?>(null);

        public Task<bool> UsernameExistsAsync(string username, CancellationToken ct)
            => Task.FromResult(false);

        public Task AddUserAsync(
            string username,
            string passwordHash,
            string role,
            long tenantId,
            CancellationToken ct)
            => Task.CompletedTask;

        public Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct)
            => Task.FromResult<UserRow?>(null);
    }
}
