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
}
