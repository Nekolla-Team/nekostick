using System.Net;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Describes the two safe outcomes of a microservice endpoint lookup.</summary>
public enum MicroserviceEndpointResolutionStatus
{
    /// <summary>A validated endpoint is available.</summary>
    Available,

    /// <summary>The service has no endpoint that may be contacted.</summary>
    Unavailable
}

/// <summary>Contains a validated microservice endpoint.</summary>
public sealed class MicroserviceEndpoint
{
    /// <summary>Creates an endpoint from an absolute HTTP or HTTPS URI.</summary>
    /// <param name="baseUri">The endpoint base URI.</param>
    public MicroserviceEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        BaseUri = Normalize(baseUri);
        DestinationPrefix = BaseUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            + BaseUri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>Creates an endpoint from an absolute HTTP or HTTPS URI string.</summary>
    /// <param name="baseAddress">The endpoint base URI string.</param>
    public MicroserviceEndpoint(string baseAddress)
        : this(CreateUri(baseAddress))
    {
    }

    /// <summary>Gets the normalized endpoint base URI, including a trailing slash.</summary>
    public Uri BaseUri { get; }

    /// <summary>Gets the safe destination prefix accepted by YARP.</summary>
    public string DestinationPrefix { get; }

    private static Uri CreateUri(string baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("An absolute HTTP endpoint is required.", nameof(baseAddress));
        }

        return uri;
    }

    private static Uri Normalize(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.HostNameType == UriHostNameType.Unknown)
        {
            throw new ArgumentException("The endpoint must be a safe absolute HTTP or HTTPS URI.", nameof(uri));
        }

        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (escapedPath.Length == 0)
        {
            escapedPath = "/";
        }

        if (escapedPath[0] != '/'
            || escapedPath.Contains('\\')
            || escapedPath.Any(character => character < 0x20 || character == '\0'))
        {
            throw new ArgumentException("The endpoint path is unsafe.", nameof(uri));
        }

        foreach (var segment in escapedPath.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".."
                || decoded.Any(character => character < 0x20 || character == 0x7f || character is '/' or '\\'))
            {
                throw new ArgumentException("The endpoint path contains an unsafe segment.", nameof(uri));
            }
        }

        if (escapedPath[^1] != '/')
        {
            escapedPath += "/";
        }

        var authority = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return new Uri(authority + escapedPath, UriKind.Absolute);
    }
}

/// <summary>Represents a safe endpoint resolution without exposing resolver details.</summary>
public sealed class MicroserviceEndpointResolution
{
    private MicroserviceEndpointResolution(
        MicroserviceEndpointResolutionStatus status,
        MicroserviceEndpoint? endpoint)
    {
        Status = status;
        Endpoint = endpoint;
    }

    /// <summary>Gets the resolution status.</summary>
    public MicroserviceEndpointResolutionStatus Status { get; }

    /// <summary>Gets the endpoint when the status is available.</summary>
    public MicroserviceEndpoint? Endpoint { get; }

    /// <summary>Gets whether a validated endpoint is available.</summary>
    public bool IsAvailable => Status == MicroserviceEndpointResolutionStatus.Available;

    /// <summary>Creates an available resolution.</summary>
    /// <param name="endpoint">The validated endpoint.</param>
    /// <returns>An available resolution.</returns>
    public static MicroserviceEndpointResolution Available(MicroserviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new(MicroserviceEndpointResolutionStatus.Available, endpoint);
    }

    /// <summary>Gets an unavailable resolution.</summary>
    public static MicroserviceEndpointResolution Unavailable { get; } =
        new(MicroserviceEndpointResolutionStatus.Unavailable, null);
}

/// <summary>Resolves the current safe endpoint for a stable microservice identifier.</summary>
public interface IMicroserviceEndpointResolver
{
    /// <summary>Resolves an endpoint without returning process or destination details.</summary>
    /// <param name="serviceId">The stable service identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A validated available or unavailable resolution.</returns>
    ValueTask<MicroserviceEndpointResolution> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Default resolver used until a host supplies a supervised endpoint resolver.</summary>
public sealed class UnavailableMicroserviceEndpointResolver : IMicroserviceEndpointResolver
{
    /// <summary>Gets the singleton unavailable resolver.</summary>
    public static UnavailableMicroserviceEndpointResolver Instance { get; } = new();

    /// <summary>Creates the default unavailable resolver.</summary>
    public UnavailableMicroserviceEndpointResolver()
    {
    }

    /// <inheritdoc />
    public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MicroserviceEndpointResolution.Unavailable);
    }
}
