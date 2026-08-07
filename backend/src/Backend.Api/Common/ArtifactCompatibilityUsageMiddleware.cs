namespace Backend.Api.Common;

public sealed class ArtifactCompatibilityUsageMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ArtifactCompatibilityUsageMetrics metrics)
    {
        if (!TryClassify(context.Request, out var surface, out var operation))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400
                && !context.Items.ContainsKey(ArtifactCompatibilityUsageMetrics.CountedItemKey))
            {
                metrics.RecordRequestFailure(surface, operation, context.Response.StatusCode);
            }
        }
        catch (Exception ex) when (!context.Items.ContainsKey(ArtifactCompatibilityUsageMetrics.CountedItemKey))
        {
            metrics.RecordRequestFailure(
                surface,
                operation,
                ex is ApiException apiException ? apiException.Status : StatusCodes.Status500InternalServerError);
            throw;
        }
    }

    private static bool TryClassify(HttpRequest request, out string surface, out string operation)
    {
        surface = request.Path.StartsWithSegments("/api/business-workflows", out var remaining)
            ? "public_business_workflows"
            : request.Path.StartsWithSegments("/api/skills", out remaining)
                ? "public_skills"
                : "";
        if (surface.Length == 0)
        {
            operation = "";
            return false;
        }

        var path = remaining.Value ?? "";
        operation = request.Method switch
        {
            "GET" when path.EndsWith("/execution-artifact", StringComparison.OrdinalIgnoreCase) => "execution_artifact",
            "GET" when path.EndsWith("/revisions", StringComparison.OrdinalIgnoreCase) => "revision_read",
            "GET" when path.EndsWith("/export", StringComparison.OrdinalIgnoreCase) => "export",
            "GET" when path.EndsWith("/package", StringComparison.OrdinalIgnoreCase) => "package",
            "GET" when path.Length == 0 || path == "/" => "list",
            "GET" => "read",
            "POST" when path.EndsWith("/restore", StringComparison.OrdinalIgnoreCase) => "revision_restore",
            "POST" when path.EndsWith("/import", StringComparison.OrdinalIgnoreCase) || path == "/import" => "import",
            "POST" => "create",
            "PUT" => "update",
            "DELETE" => "delete",
            _ => "",
        };
        return operation.Length != 0;
    }
}
