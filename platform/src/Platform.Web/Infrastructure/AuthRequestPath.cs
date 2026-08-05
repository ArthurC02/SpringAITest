namespace Platform.Web.Infrastructure;

internal enum AuthWriteOperation
{
    Login,
    Register,
}

internal static class AuthRequestPath
{
    internal static bool TryMatch(HttpRequest request, out AuthWriteOperation operation)
    {
        operation = default;
        if (!HttpMethods.IsPost(request.Method) || request.Path.Value is not { } path)
        {
            return false;
        }

        if (path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path[..^1];
        }

        if (string.Equals(path, "/api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            operation = AuthWriteOperation.Login;
            return true;
        }

        if (string.Equals(path, "/api/auth/register", StringComparison.OrdinalIgnoreCase))
        {
            operation = AuthWriteOperation.Register;
            return true;
        }

        return false;
    }
}
