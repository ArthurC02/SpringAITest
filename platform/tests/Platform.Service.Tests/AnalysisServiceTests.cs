using System.Net;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class AnalysisServiceTests
{
    private static readonly UserContext Ctx = new("alice", "demo-a", "USER");

    private static AnalysisService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    [Fact]
    public async Task Summary_ForwardsTenant_MapsSnakeCaseResponse()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
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

    // 「沒有回應」與「回了 500」是兩條不同的程式路徑(WrapTransport vs mapError),對外同為 502。
    [Fact]
    public async Task Summary_TransportFailure_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("backend 不可達")));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.SummaryAsync(Ctx));
        Assert.StartsWith("分析服務呼叫失敗：", ex.Message);
    }

    // 2xx 但沒有可用 body 的等價類:backend 回 JSON null(走 onEmptyBody)或整包沒有 body(解析失敗),
    // 兩者都必須收斂成受控 502(帶 FailurePrefix),不得被當成查詢成功而回一個半空的統計摘要。
    [Theory]
    [InlineData("null", "分析服務呼叫失敗：回應內容為空")]
    [InlineData("", "分析服務呼叫失敗：")]
    public async Task Summary_Backend2xxWithoutUsableBody_ThrowsControlled502(string body, string expectedPrefix)
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, body)));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.SummaryAsync(Ctx));

        Assert.StartsWith(expectedPrefix, ex.Message);
    }
}
