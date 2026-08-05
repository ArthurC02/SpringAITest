using System.Buffers;
using Platform.Web.Errors;

namespace Platform.Web.Infrastructure;

internal sealed class AuthRequestBodyLimitMiddleware(RequestDelegate next)
{
    internal const int MaximumBodyBytes = 16 * 1024;
    internal const string RejectionMessage = "認證請求內容過大";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!AuthRequestPath.TryMatch(context.Request, out _))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength is > MaximumBodyBytes)
        {
            await WriteRejectedAsync(context);
            return;
        }

        if (context.Request.ContentLength is not null)
        {
            await next(context);
            return;
        }

        var originalBody = context.Request.Body;
        await using var boundedBody = new MemoryStream(capacity: MaximumBodyBytes);
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (boundedBody.Length < MaximumBodyBytes)
            {
                var remaining = MaximumBodyBytes - (int)boundedBody.Length;
                var read = await originalBody.ReadAsync(
                    rented.AsMemory(0, Math.Min(rented.Length, remaining)),
                    context.RequestAborted);
                if (read == 0)
                {
                    boundedBody.Position = 0;
                    context.Request.Body = boundedBody;
                    await next(context);
                    return;
                }

                await boundedBody.WriteAsync(rented.AsMemory(0, read), context.RequestAborted);
            }

            var extra = await originalBody.ReadAsync(rented.AsMemory(0, 1), context.RequestAborted);
            if (extra != 0)
            {
                await WriteRejectedAsync(context);
                return;
            }

            boundedBody.Position = 0;
            context.Request.Body = boundedBody;
            await next(context);
        }
        finally
        {
            context.Request.Body = originalBody;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static Task WriteRejectedAsync(HttpContext context)
        => ApiErrorWriter.WriteAsync(
            context.Response,
            StatusCodes.Status413PayloadTooLarge,
            RejectionMessage,
            context.RequestAborted);
}
