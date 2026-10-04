namespace Nekolla.Nekostick.Contracts;

/// <summary>Contains a precise, human-readable cause for an extension API failure.</summary>
/// <remarks>
/// Instances are created only for failure paths. <see cref="FromException(Exception)" /> copies the
/// exception message, using its type name when the message is blank, the full type name, and at most one
/// inner exception message without retaining the exception object or its stack trace.
/// </remarks>
public sealed record ExtensionErrorDetail
{
    /// <summary>Creates a precise error detail.</summary>
    /// <param name="message">The required non-whitespace human-readable cause of the failure.</param>
    /// <param name="exceptionType">The full exception type name, or <see langword="null" /> when the failure is not exception-derived.</param>
    /// <param name="innerDetail">The inner exception message or other additional context, when available.</param>
    public ExtensionErrorDetail(
        string message,
        string? exceptionType = null,
        string? innerDetail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Message = message;
        ExceptionType = exceptionType;
        InnerDetail = innerDetail;
    }

    /// <summary>Gets the required, human-readable precise cause of the failure.</summary>
    public string Message { get; }

    /// <summary>Gets the full exception type name when the failure came from an exception.</summary>
    public string? ExceptionType { get; }

    /// <summary>Gets the direct inner exception message or other additional failure context.</summary>
    public string? InnerDetail { get; }

    /// <summary>Creates error detail from an exception and its direct inner exception.</summary>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <returns>A newly allocated detail containing the exception message or its type name when blank, the full type name, and at most one inner message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception" /> is <see langword="null" />.</exception>
    public static ExtensionErrorDetail FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var exceptionType = exception.GetType();
        var exceptionMessage = exception.Message;
        return new ExtensionErrorDetail(
            string.IsNullOrWhiteSpace(exceptionMessage)
                ? exceptionType.FullName ?? exceptionType.Name
                : exceptionMessage,
            exceptionType.FullName,
            exception.InnerException?.Message);
    }
}
