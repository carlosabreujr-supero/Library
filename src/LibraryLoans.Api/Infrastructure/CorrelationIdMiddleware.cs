using System.Diagnostics;

namespace LibraryLoans.Api.Infrastructure;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        // OnStarting: o ExceptionHandler limpa os headers da resposta antes de escrever o erro.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        // Aceita apenas valores curtos e seguros para evitar log injection.
        if (incoming.Length is > 0 and <= 64 && incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return incoming;

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }
}
