namespace MeridianVoteTracker.Application.Common;

public enum UseCaseErrorKind
{
    Validation,
    NotFound,
    UnsupportedMediaType,
    Conflict,
}

/// <summary>
/// An expected, user-facing failure of a use case. The API maps <see cref="Kind"/> to an HTTP status and
/// renders ProblemDetails (with <see cref="Field"/> as a validation error key when present), so use cases
/// stay free of HTTP concerns.
/// </summary>
public sealed class UseCaseException(UseCaseErrorKind kind, string code, string message, string? field = null) : Exception(message)
{
    public UseCaseErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    public string? Field { get; } = field;

    public static UseCaseException Validation(string field, string code, string message) => new(UseCaseErrorKind.Validation, code, message, field);

    public static UseCaseException NotFound(string code, string message) => new(UseCaseErrorKind.NotFound, code, message);
}
