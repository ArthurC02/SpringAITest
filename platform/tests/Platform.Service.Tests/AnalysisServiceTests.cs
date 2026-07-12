using System.Net;
using System.Text;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class AnalysisServiceTests
{
    private static readonly UserContext Ctx = new("alice", "demo-a", "USER");

    private static AnalysisService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Summary_ForwardsTenant_MapsSnakeCaseResponse()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            "{\"document_count\":3,\"chunk_count\":12,\"latest_titles\":[\"甲\",\"乙\"]}"));
        var svc = Build(stub);

        var summary = await svc.SummaryAsync(Ctx);

        Assert.Equal(3, summary.DocumentCount);
        Assert.Equal(12, summary.ChunkCount);
        Assert.Equal(new[] { "甲", "乙" }, summary.LatestTitles);

        Assert.Equal("http://backend/api/analysis/summary", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("alice", stub.Header("X-User-Id"));
        Assert.Equal("USER", stub.Header("X-User-Role"));
    }

    [Fact]
    public async Task Summary_500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.SummaryAsync(Ctx));
    }
}
