namespace MeridianVoteTracker.Api.Middleware;

/// <summary>
/// Accepts an incoming X-Correlation-Id (or generates one), echoes it in the response and
/// opens a logging scope so every log line of the request carries it. Background jobs will
/// persist the id on the job row so worker logs can be correlated with the upload request.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var isUsable = !string.IsNullOrWhiteSpace(incoming)
            && incoming.Length <= MaxLength
            && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        return isUsable ? incoming : Guid.NewGuid().ToString("N");
    }
}
