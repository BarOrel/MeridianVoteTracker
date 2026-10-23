namespace MeridianVoteTracker.Api.ErrorHandling;

public static class ProblemDetailsSetup
{
    /// <summary>
    /// Uniform RFC 7807 error format for the whole API: unhandled exceptions, empty error
    /// status codes (404, 405...) and model validation all return application/problem+json
    /// with the correlation id, so a UI error can be traced to the server logs.
    /// </summary>
    public static IServiceCollection AddUniformProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
                context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
            };
        });
}
