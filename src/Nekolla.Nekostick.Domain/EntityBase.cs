namespace Nekolla.Nekostick.Domain;

/// <summary>Provides UUID, UTC timestamps, and bigint-style optimistic version state.</summary>
public abstract class EntityBase
{
    /// <summary>Creates a new entity using a UUID v7 generator and UTC time provider.</summary>
    /// <param name="uuidGenerator">The UUID v7 generator.</param>
    /// <param name="timeProvider">The time provider, or the system provider.</param>
    protected EntityBase(IUuidV7Generator uuidGenerator, TimeProvider? timeProvider = null)
        : this(
            uuidGenerator?.Create() ?? throw new ArgumentNullException(nameof(uuidGenerator)),
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            1)
    {
    }

    /// <summary>Rehydrates an entity with persisted identity and concurrency state.</summary>
    /// <param name="id">The UUID v7 identifier.</param>
    /// <param name="createdAt">The creation timestamp.</param>
    /// <param name="version">The bigint-style optimistic version.</param>
    protected EntityBase(Guid id, DateTimeOffset createdAt, long version)
    {
        UuidV7.RequireVersion7(id, nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        Id = id;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
        Version = version;
    }

    /// <summary>Gets the public UUID v7 identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Gets the bigint-style optimistic-concurrency version.</summary>
    public long Version { get; private set; }

    /// <summary>Advances the UTC timestamp and optimistic version after a domain change.</summary>
    /// <param name="updatedAt">The new UTC timestamp.</param>
    protected void Touch(DateTimeOffset updatedAt)
    {
        var utc = updatedAt.ToUniversalTime();
        if (utc < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAt));
        }

        UpdatedAt = utc;
        Version = checked(Version + 1);
    }
}
