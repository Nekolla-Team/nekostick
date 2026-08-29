namespace Nekolla.Nekostick.Extensions;

/// <summary>Thrown when an extension streaming request body exceeds its configured limit.</summary>
internal sealed class ExtensionRequestBodyLimitExceededException : IOException
{
}

/// <summary>Thrown when reading an extension streaming request body exceeds its configured deadline.</summary>
internal sealed class ExtensionRequestReadTimeoutException : OperationCanceledException
{
}
