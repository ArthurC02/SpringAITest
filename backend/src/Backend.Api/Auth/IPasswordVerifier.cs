namespace Backend.Api.Auth;

public interface IPasswordVerifier
{
    bool Verify(string password, string passwordHash);
}

public sealed class BCryptPasswordVerifier : IPasswordVerifier
{
    public static BCryptPasswordVerifier Instance { get; } = new();

    private BCryptPasswordVerifier()
    {
    }

    public bool Verify(string password, string passwordHash)
        => BCrypt.Net.BCrypt.Verify(password, passwordHash);
}
