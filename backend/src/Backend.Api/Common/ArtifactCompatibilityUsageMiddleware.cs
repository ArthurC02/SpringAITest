using Microsoft.AspNetCore.Mvc.Controllers;

namespace Backend.Api.Common;

public sealed class ArtifactCompatibilityUsageMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ArtifactCompatibilityUsageMetrics metrics)
    {
        // Route matching is authoritative. Path-shaped probes must not become compatibility
        // evidence, even when their inferred surface/operation pair happens to be valid.
        if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is null
            || !TryClassify(context.Request, out var surface, out var operation))
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
        // 本 middleware 刻意跑在 InternalTokenMiddleware 之前,任何人(含無 token 的探測)都能觸發分類;
        // 方法+路徑尾綴不足以判斷該 surface 真的有這條路由(例:/api/business-workflows/x/revisions 並不存在,
        // BusinessWorkflowController 只有 list/read/create/update/delete/export)。不是本 surface 權威的
        // operation 一律不記錄 —— 記了會讓匯出器對整個觀測窗 ExportError(見 Metrics 的 SurfaceOperations)。
        return operation.Length != 0
            && ArtifactCompatibilityUsageMetrics.IsAuthoritative(surface, operation);
    }
}
