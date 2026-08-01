using Microsoft.AspNetCore.Http;
using Platform.Service.Exceptions;
using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class HttpChatIdentityAccessorTests
{
    [Fact]
    public void LogicalAttemptId_UsesOneTrimmedInboundIdempotencyHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "  retry-attempt-1  ";
        var accessor = new HttpChatIdentityAccessor(
            new HttpContextAccessor { HttpContext = context });

        Assert.Equal("retry-attempt-1", accessor.LogicalAttemptId);
    }

    // 512 是 MaxLogicalAttemptIdLength 的 on-point(下一案的 513 是 off-point):恰好上限必須被接受,
    // 否則「打錯一個字」的長度上限不會有測試變紅。
    [Fact]
    public void LogicalAttemptId_At512Chars_IsAccepted()
    {
        var context = new DefaultHttpContext();
        var key = new string('x', 512);
        context.Request.Headers["Idempotency-Key"] = key;
        var accessor = new HttpChatIdentityAccessor(new HttpContextAccessor { HttpContext = context });

        Assert.Equal(key, accessor.LogicalAttemptId);
    }

    // 只有空白的 header:Trim 後為空 → 視為無效輸入而拒絕(不可退化成「當作沒帶」而靜默放行)。
    // (真正的空字串 header 到不了這裡:HeaderDictionary 的 setter 對空值等同移除該 header。)
    [Fact]
    public void LogicalAttemptId_WhitespaceOnlyHeader_Rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "   ";
        var accessor = new HttpChatIdentityAccessor(new HttpContextAccessor { HttpContext = context });

        Assert.Throws<WorkflowBadInputException>(() => _ = accessor.LogicalAttemptId);
    }

    // 完全沒帶 header 的預設情況(Count == 0):必須回 null 而不是丟例外 —— 一般聊天請求都走這條,
    // 若它退化成「無效輸入」等於每一輪都 400。
    [Fact]
    public void LogicalAttemptId_HeaderAbsent_IsNull()
    {
        var context = new DefaultHttpContext();
        var accessor = new HttpChatIdentityAccessor(new HttpContextAccessor { HttpContext = context });

        Assert.Null(accessor.LogicalAttemptId);
    }

    [Fact]
    public void LogicalAttemptId_RejectsAmbiguousOrOversizedInboundHeaders()
    {
        var ambiguous = new DefaultHttpContext();
        ambiguous.Request.Headers.Append("Idempotency-Key", "one");
        ambiguous.Request.Headers.Append("Idempotency-Key", "two");
        var ambiguousAccessor = new HttpChatIdentityAccessor(
            new HttpContextAccessor { HttpContext = ambiguous });
        Assert.Throws<WorkflowBadInputException>(() => _ = ambiguousAccessor.LogicalAttemptId);

        var oversized = new DefaultHttpContext();
        oversized.Request.Headers["Idempotency-Key"] = new string('x', 513);
        var oversizedAccessor = new HttpChatIdentityAccessor(
            new HttpContextAccessor { HttpContext = oversized });
        Assert.Throws<WorkflowBadInputException>(() => _ = oversizedAccessor.LogicalAttemptId);
    }

    // ---- X-Orchestrator-Id:前端(App.tsx)已在送,platform 端先前零測試 ----

    private static readonly Guid BodySelection = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HeaderSelection = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static HttpChatIdentityAccessor Accessor(HttpContext context) =>
        new(new HttpContextAccessor { HttpContext = context });

    [Fact]
    public void RequestedOrchestratorId_HeaderGuid_IsUsed_WhenBodyValueAbsent()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Orchestrator-Id"] = $"  {HeaderSelection:D}  ";
        var accessor = Accessor(context);
        accessor.SetRequestedOrchestratorId(null); // ChatService 每輪都會這樣呼叫(body 沒帶時傳 null)。

        Assert.Equal(HeaderSelection, accessor.RequestedOrchestratorId);
    }

    // 空白 header 等同沒指定 → null → 留在 legacy 選擇路徑(不得因此拒絕整個請求)。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RequestedOrchestratorId_BlankHeader_IsNull(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Orchestrator-Id"] = header;
        var accessor = Accessor(context);
        accessor.SetRequestedOrchestratorId(null);

        Assert.Null(accessor.RequestedOrchestratorId);
    }

    [Fact]
    public void RequestedOrchestratorId_MalformedHeader_Throws()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Orchestrator-Id"] = "not-a-guid";
        var accessor = Accessor(context);
        accessor.SetRequestedOrchestratorId(null);

        Assert.Throws<WorkflowBadInputException>(() => _ = accessor.RequestedOrchestratorId);
    }

    // body 的 orchestratorId 與 header 衝突時 body 勝(controller 先寫入 Items,header 只是沒帶 body 時的來源)。
    [Fact]
    public void RequestedOrchestratorId_BodyValueWinsOverConflictingHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Orchestrator-Id"] = HeaderSelection.ToString("D");
        var accessor = Accessor(context);
        accessor.SetRequestedOrchestratorId(BodySelection);

        Assert.Equal(BodySelection, accessor.RequestedOrchestratorId);
    }

    // body 有值時 header 根本不會被解析:即使 header 是壞掉的字串也不得丟例外(Items 命中就短路返回)。
    // 沒有這一案,把短路搬到解析之後的改動會靜默通過。
    [Fact]
    public void RequestedOrchestratorId_BodyValueWins_WithoutParsingMalformedHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Orchestrator-Id"] = "not-a-guid";
        var accessor = Accessor(context);
        accessor.SetRequestedOrchestratorId(BodySelection);

        Assert.Equal(BodySelection, accessor.RequestedOrchestratorId);
    }
}
