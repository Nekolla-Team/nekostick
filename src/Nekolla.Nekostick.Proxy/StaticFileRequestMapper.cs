namespace Nekolla.Nekostick.Proxy;

/// <summary>Maps routing-normalized request paths into a static target without performing HTTP I/O.</summary>
public static class StaticFileRequestMapper
{
    /// <summary>
    /// Resolves a routing-normalized absolute request path against a static target.
    /// The path is kept in its original percent-encoded form when constructing the filesystem target.
    /// </summary>
    /// <param name="target">The immutable static target definition.</param>
    /// <param name="normalizedRequestPath">The normalized absolute request path.</param>
    /// <returns>A typed resolution result with no user path in its diagnostics or string representation.</returns>
    public static StaticFileResolution Map(
        StaticTargetDefinition target,
        string normalizedRequestPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Resolve(normalizedRequestPath);
    }

    /// <summary>
    /// Resolves a routing-normalized request path after applying the static target method policy.
    /// Only <c>GET</c> and <c>HEAD</c> are accepted by this pure core.
    /// </summary>
    /// <param name="target">The immutable static target definition.</param>
    /// <param name="method">The HTTP method token.</param>
    /// <param name="normalizedRequestPath">The normalized absolute request path.</param>
    /// <returns>A typed resolution result with no user path in its diagnostics or string representation.</returns>
    public static StaticFileResolution Map(
        StaticTargetDefinition target,
        string method,
        string normalizedRequestPath)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (method is null)
        {
            return target.CreateInvalidResolution(StaticFileFailureReason.UnsupportedMethod);
        }

        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return target.CreateForbiddenResolution(StaticFileFailureReason.UnsupportedMethod);
        }

        return target.Resolve(normalizedRequestPath);
    }
}
