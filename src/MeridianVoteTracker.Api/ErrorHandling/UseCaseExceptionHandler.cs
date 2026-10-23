using MeridianVoteTracker.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.ErrorHandling;

/// <summary>
/// Maps expected use-case failures to ProblemDetails with a precise status. Validation errors use the
/// ValidationProblemDetails shape (errors keyed by field) so the UI can show them next to the field.
/// Anything else falls through to the default handler (500, no internals leaked).
/// </summary>
public sealed class UseCaseExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not UseCaseException failure)
        {
            return false;
        }

        var status = failure.Kind switch
        {
            UseCaseErrorKind.Validation => StatusCodes.Status400BadRequest,
            UseCaseErrorKind.NotFound => StatusCodes.Status404NotFound,
            UseCaseErrorKind.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
            UseCaseErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        ProblemDetails details = failure.Field is null
            ? new ProblemDetails()
            : new ValidationProblemDetails(new Dictionary<string, string[]> { [failure.Field] = [failure.Message] });
        details.Status = status;
        details.Title = failure.Message;
        details.Detail = failure.Message;
        details.Extensions["code"] = failure.Code;

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }
}
