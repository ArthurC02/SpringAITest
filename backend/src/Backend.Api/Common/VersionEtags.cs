namespace Backend.Api.Common;

public static class VersionEtags
{
    public static long RequireIfMatchVersion(this HttpRequest request)
    {
        var raw = request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw)) throw new ApiException(StatusCodes.Status428PreconditionRequired, "If-Match header is required");
        if (!long.TryParse(raw, out var version) || version < 1) throw new ApiException(StatusCodes.Status400BadRequest, "If-Match header is invalid");
        return version;
    }

    public static void SetVersionETag(this HttpResponse response, long version) => response.Headers.ETag = $"\"{version}\"";
}
