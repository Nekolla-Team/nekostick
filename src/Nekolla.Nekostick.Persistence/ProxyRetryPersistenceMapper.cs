using System.IO;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Maps persisted proxy retry columns to and from the immutable retry contract.</summary>
public static class ProxyRetryPersistenceMapper
{
    /// <summary>Maps persisted retry columns to a validated immutable policy.</summary>
    /// <exception cref="InvalidDataException">The persisted retry columns are invalid.</exception>
    public static ProxyRetryConfiguration ToContract(
        int maxRetries,
        int initialBackoffMilliseconds,
        int maximumBackoffMilliseconds,
        bool retryOnConnectionFailure,
        bool retryOnUpstreamDisconnect)
    {
        try
        {
            return new ProxyRetryConfiguration(
                maxRetries,
                TimeSpan.FromMilliseconds(initialBackoffMilliseconds),
                TimeSpan.FromMilliseconds(maximumBackoffMilliseconds),
                retryOnConnectionFailure,
                retryOnUpstreamDisconnect);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Persisted proxy retry settings are invalid.");
        }
    }
    /// <summary>Maps nullable route retry columns to an optional validated immutable policy.</summary>
    /// <exception cref="InvalidDataException">The persisted route retry columns are incomplete or invalid.</exception>
    public static ProxyRetryConfiguration? ToNullableContract(
        int? maxRetries,
        int? initialBackoffMilliseconds,
        int? maximumBackoffMilliseconds,
        bool? retryOnConnectionFailure,
        bool? retryOnUpstreamDisconnect)
    {
        var allNull = maxRetries is null &&
            initialBackoffMilliseconds is null &&
            maximumBackoffMilliseconds is null &&
            retryOnConnectionFailure is null &&
            retryOnUpstreamDisconnect is null;
        if (allNull)
        {
            return null;
        }

        if (maxRetries is null || initialBackoffMilliseconds is null ||
            maximumBackoffMilliseconds is null || retryOnConnectionFailure is null ||
            retryOnUpstreamDisconnect is null)
        {
            throw new InvalidDataException("Persisted route proxy retry settings are incomplete.");
        }

        return ToContract(
            maxRetries.Value,
            initialBackoffMilliseconds.Value,
            maximumBackoffMilliseconds.Value,
            retryOnConnectionFailure.Value,
            retryOnUpstreamDisconnect.Value);
    }


    /// <summary>Maps an immutable policy to persisted scalar columns.</summary>
    public static (int MaxRetries,
        int InitialBackoffMilliseconds,
        int MaximumBackoffMilliseconds,
        bool RetryOnConnectionFailure,
        bool RetryOnUpstreamDisconnect) ToPersistence(ProxyRetryConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (
            value.MaxRetries,
            checked((int)value.InitialBackoff.TotalMilliseconds),
            checked((int)value.MaximumBackoff.TotalMilliseconds),
            value.RetryOnConnectionFailure,
            value.RetryOnUpstreamDisconnect);
    }
    /// <summary>Maps an optional route retry policy to nullable persisted scalar columns.</summary>
    public static (int? MaxRetries,
        int? InitialBackoffMilliseconds,
        int? MaximumBackoffMilliseconds,
        bool? RetryOnConnectionFailure,
        bool? RetryOnUpstreamDisconnect) ToNullablePersistence(ProxyRetryConfiguration? value)
    {
        if (value is null)
        {
            return (null, null, null, null, null);
        }

        var persisted = ToPersistence(value);
        return (
            persisted.MaxRetries,
            persisted.InitialBackoffMilliseconds,
            persisted.MaximumBackoffMilliseconds,
            persisted.RetryOnConnectionFailure,
            persisted.RetryOnUpstreamDisconnect);
    }

}
