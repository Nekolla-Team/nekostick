using System.Net;
using System.Net.Http;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Owns a bounded pool of timeout-keyed outbound HTTP invokers.</summary>
public sealed class MicroserviceHttpInvokerPool : IDisposable
{
    private const int MaximumKeyCount = 4;
    private static readonly TimeSpan MaximumConnectTimeout =
        TimeSpan.FromMilliseconds(int.MaxValue);
    private readonly object _gate = new();
    private readonly Dictionary<TimeSpan, Entry> _entries = new();
    private long _lastUse;
    private bool _disposed;

    /// <summary>Acquires an invoker keyed by its connection timeout when capacity permits.</summary>
    public bool TryAcquire(
        TimeSpan connectTimeout,
        out MicroserviceHttpInvokerLease? lease)
    {
        if (connectTimeout <= TimeSpan.Zero || connectTimeout > MaximumConnectTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        Entry? retired = null;
        lease = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_entries.TryGetValue(connectTimeout, out var existing))
            {
                existing.ReferenceCount++;
                existing.LastUse = ++_lastUse;
                lease = new MicroserviceHttpInvokerLease(this, existing);
                return true;
            }

            if (_entries.Count >= MaximumKeyCount)
            {
                retired = _entries.Values
                    .Where(entry => entry.ReferenceCount == 0)
                    .OrderBy(entry => entry.LastUse)
                    .FirstOrDefault();
                if (retired is null)
                {
                    return false;
                }

                _entries.Remove(retired.ConnectTimeout);
                retired.Retired = true;
            }
        }

        Entry created;
        try
        {
            created = CreateEntry(connectTimeout);
        }
        catch
        {
            retired?.DisposeResources();
            throw;
        }

        Entry? racedRetired = null;
        var disposeCreated = false;
        var acquired = false;
        lock (_gate)
        {
            if (_disposed)
            {
                created.Retired = true;
                disposeCreated = true;
            }
            else if (_entries.TryGetValue(connectTimeout, out var existing))
            {
                existing.ReferenceCount++;
                existing.LastUse = ++_lastUse;
                lease = new MicroserviceHttpInvokerLease(this, existing);
                disposeCreated = true;
                acquired = true;
            }
            else
            {
                if (_entries.Count >= MaximumKeyCount)
                {
                    racedRetired = _entries.Values
                        .Where(entry => entry.ReferenceCount == 0)
                        .OrderBy(entry => entry.LastUse)
                        .FirstOrDefault();
                    if (racedRetired is null)
                    {
                        created.Retired = true;
                        disposeCreated = true;
                    }
                    else
                    {
                        _entries.Remove(racedRetired.ConnectTimeout);
                        racedRetired.Retired = true;
                    }
                }

                if (!disposeCreated)
                {
                    created.ReferenceCount = 1;
                    created.LastUse = ++_lastUse;
                    _entries.Add(connectTimeout, created);
                    lease = new MicroserviceHttpInvokerLease(this, created);
                    acquired = true;
                }
            }
        }

        retired?.DisposeResources();
        racedRetired?.DisposeResources();
        if (disposeCreated)
        {
            created.DisposeResources();
        }

        return acquired;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<Entry> dispose;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispose = [];
            foreach (var entry in _entries.Values)
            {
                entry.Retired = true;
                if (entry.ReferenceCount == 0)
                {
                    dispose.Add(entry);
                }
            }

            _entries.Clear();
        }

        foreach (var entry in dispose)
        {
            entry.DisposeResources();
        }
    }

    internal void Release(Entry entry)
    {
        Entry? dispose = null;
        lock (_gate)
        {
            if (entry.ReferenceCount > 0)
            {
                entry.ReferenceCount--;
            }

            if (entry.Retired && entry.ReferenceCount == 0)
            {
                dispose = entry;
            }
        }

        dispose?.DisposeResources();
    }

    private static Entry CreateEntry(TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            PreAuthenticate = false,
            ConnectTimeout = connectTimeout
        };
        return new Entry(connectTimeout, handler, new HttpMessageInvoker(handler, disposeHandler: true));
    }

    internal sealed class Entry
    {
        internal Entry(
            TimeSpan connectTimeout,
            SocketsHttpHandler handler,
            HttpMessageInvoker invoker)
        {
            ConnectTimeout = connectTimeout;
            Handler = handler;
            Invoker = invoker;
        }

        internal TimeSpan ConnectTimeout { get; }
        internal SocketsHttpHandler Handler { get; }
        internal HttpMessageInvoker Invoker { get; }
        internal int ReferenceCount { get; set; }
        internal long LastUse { get; set; }
        internal bool Retired { get; set; }

        internal void DisposeResources() => Invoker.Dispose();
    }
}

/// <summary>Represents one reference-counted invoker pool lease.</summary>
public sealed class MicroserviceHttpInvokerLease : IDisposable
{
    private readonly MicroserviceHttpInvokerPool _owner;
    private MicroserviceHttpInvokerPool.Entry? _entry;

    internal MicroserviceHttpInvokerLease(
        MicroserviceHttpInvokerPool owner,
        MicroserviceHttpInvokerPool.Entry entry)
    {
        _owner = owner;
        _entry = entry;
    }

    /// <summary>Gets the leased invoker.</summary>
    public HttpMessageInvoker Invoker =>
        _entry?.Invoker ?? throw new ObjectDisposedException(nameof(MicroserviceHttpInvokerLease));

    /// <inheritdoc />
    public void Dispose()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is not null)
        {
            _owner.Release(entry);
        }
    }
}
