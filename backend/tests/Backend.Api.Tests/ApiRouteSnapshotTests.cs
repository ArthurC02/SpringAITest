using Backend.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

/// <summary>
/// 這份快照同時是**授權狀態的 review artifact**:每一行除了 method + path,還記錄該端點的
/// AllowAnonymous / IAuthorizeData / <see cref="AdminOnlyAttribute"/> 狀態。任何一行
/// <c>[authz:...]</c> 欄位變動(拿掉 policy、誤加 AllowAnonymous、拿掉 AdminOnly、改成僅需登入)
/// 都會讓這支測試變紅,必須在 PR 中被明確 review,不能只靠逐端點的 API 測試碰巧覆蓋到。
/// backend 沒有 ASP.NET 內建的 authorization middleware,真正的每路由授權邊界是
/// <see cref="AdminOnlyAttribute"/>(authorization filter,不是 <c>IAuthorizeData</c>)加上
/// handler 內的 <c>RequireCapability()</c>/<c>RequireTenant()</c> 呼叫;<c>[authz:...]</c>
/// 只反映前兩者,handler 內的 capability/tenant 檢查不在此快照覆蓋範圍內。
/// platform 側的 Platform.Web.Tests.ApiRouteSnapshotTests 是刻意的近似複本:兩邊字串格式必須
/// 完全一致,但不抽共用(全 repo 測試框架化是已被否決的提案)。
/// </summary>
public sealed class ApiRouteSnapshotTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ApiRouteSnapshotTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public void RegisteredApiRoutes_MatchReviewedSnapshot()
    {
        using var client = _factory.CreateClient();
        var actual = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var path = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    ?? ["ANY"];
                var authz = Authorization(endpoint);
                return methods.Select(method => $"{method.ToUpperInvariant()} {path} {authz}");
            })
            .Where(route => route[(route.IndexOf(' ') + 1)..].StartsWith("/api/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var snapshotPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "RouteSnapshots", "backend-api.txt"));
        if (Environment.GetEnvironmentVariable("UPDATE_ROUTE_SNAPSHOTS") == "1")
        {
            File.WriteAllLines(snapshotPath, actual);
            return;
        }
        var expected = File.ReadAllLines(snapshotPath);
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Backend API route snapshot drifted. Review the contract and update {snapshotPath} intentionally.\n"
            + string.Join('\n', actual));
    }

    private static string Authorization(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<AllowAnonymousAttribute>() is not null)
        {
            return "[authz:anonymous]";
        }

        if (endpoint.Metadata.GetMetadata<AdminOnlyAttribute>() is not null)
        {
            return "[authz:admin-only]";
        }

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (authorize.Count == 0)
        {
            return "[authz:none]";
        }

        var policies = Distinct(authorize.Select(data => data.Policy));
        if (policies.Length > 0)
        {
            return $"[authz:policy={string.Join('+', policies)}]";
        }

        var roles = Distinct(authorize.Select(data => data.Roles));
        return roles.Length > 0
            ? $"[authz:roles={string.Join('+', roles)}]"
            : "[authz:authenticated]";
    }

    private static string[] Distinct(IEnumerable<string?> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
