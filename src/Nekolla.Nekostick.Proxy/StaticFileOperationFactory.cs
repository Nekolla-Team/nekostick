namespace Nekolla.Nekostick.Proxy;

internal static class StaticFileOperationFactory
{
    private static readonly Lazy<IStaticFileOperation> CachedOperation =
        new(CreateOperation);

    internal static IStaticFileOperation Create() => CachedOperation.Value;

    private static IStaticFileOperation CreateOperation()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxStaticFileOperation.Create();
            }

            if (OperatingSystem.IsMacOS())
            {
                return DarwinStaticFileOperation.Create();
            }
        }
        catch (Exception)
        {
        }

        return new UnsupportedStaticFileOperation();
    }
}

internal sealed class UnsupportedStaticFileOperation : IStaticFileOperation
{
    public StaticFileOperationResult OpenReadOnly(string canonicalRootPath, string canonicalTargetPath) =>
        StaticFileOperationResult.FromStatus(StaticFileOperationStatus.UnsupportedAbi);
}
