namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
    internal StaticFileResolution CreateInvalidResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.Invalid, reason, null, null, null);

    internal StaticFileResolution CreateForbiddenResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.Forbidden, reason, null, null, null);

    private StaticFileResolution CreateNotFoundResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.NotFound, reason, null, null, null);

    private StaticFileResolution CreateResolution(
        StaticFileResolutionKind kind,
        StaticFileFailureReason failureReason,
        string? lexicalPath,
        string? canonicalPath,
        string? canonicalRootPath) =>
        new(
            this,
            kind,
            failureReason,
            lexicalPath,
            canonicalPath,
            canonicalRootPath,
            kind is StaticFileResolutionKind.FoundFile or StaticFileResolutionKind.DirectoryIndexCandidate
                ? StaticContentTypeMap.GetContentType(canonicalPath!)
                : null);
}
