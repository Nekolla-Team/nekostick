namespace Nekolla.Nekostick.Domain;

/// <summary>Describes the lifecycle state visible for an extension.</summary>
public enum ExtensionLoadState
{
    /// <summary>The extension was discovered but is not running.</summary>
    Discovered,

    /// <summary>The extension is serving.</summary>
    Loaded,

    /// <summary>The extension is stopped.</summary>
    Stopped,

    /// <summary>The extension failed validation or execution.</summary>
    Failed,

    /// <summary>The extension is being unloaded.</summary>
    Unloading
}

/// <summary>Contains a stable extension identifier.</summary>
public readonly record struct ExtensionIdentifier
{
    /// <summary>Creates an extension identifier.</summary>
    /// <param name="value">The stable identifier text.</param>
    public ExtensionIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A safe extension identifier is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the identifier text.</summary>
    public string Value { get; }

    /// <summary>Returns the identifier text.</summary>
    /// <returns>The identifier value.</returns>
    public override string ToString() => Value;
}

/// <summary>Contains the numeric core of an extension semantic version.</summary>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    /// <summary>Creates a semantic version.</summary>
    /// <param name="major">The major version.</param>
    /// <param name="minor">The minor version.</param>
    /// <param name="patch">The patch version.</param>
    public SemanticVersion(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>Gets the major version.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version.</summary>
    public int Patch { get; }

    /// <summary>Compares semantic versions numerically.</summary>
    /// <param name="other">The version to compare.</param>
    /// <returns>The comparison result.</returns>
    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Determines whether one semantic version precedes another.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    /// <summary>Determines whether one semantic version does not exceed another.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Determines whether one semantic version follows another.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    /// <summary>Determines whether one semantic version is not below another.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;

    /// <summary>Returns semantic version text.</summary>
    /// <returns>The major.minor.patch text.</returns>
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Describes the domain portion of an extension record.</summary>
public sealed class ExtensionDefinition : EntityBase
{
    /// <summary>Creates an extension definition.</summary>
    /// <param name="uuidGenerator">The UUID v7 generator.</param>
    /// <param name="identifier">The stable extension identifier.</param>
    /// <param name="version">The extension semantic version.</param>
    /// <param name="timeProvider">The UTC time provider.</param>
    public ExtensionDefinition(
        IUuidV7Generator uuidGenerator,
        ExtensionIdentifier identifier,
        SemanticVersion version,
        TimeProvider? timeProvider = null)
        : base(uuidGenerator, timeProvider)
    {
        Identifier = identifier;
        ExtensionVersion = version;
        LoadState = ExtensionLoadState.Discovered;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public ExtensionIdentifier Identifier { get; }

    /// <summary>Gets the installed extension version.</summary>
    public SemanticVersion ExtensionVersion { get; }

    /// <summary>Gets the current load state.</summary>
    public ExtensionLoadState LoadState { get; private set; }

    /// <summary>Changes the state after a host lifecycle transition.</summary>
    /// <param name="state">The new load state.</param>
    /// <param name="updatedAt">The transition timestamp.</param>
    public void SetLoadState(ExtensionLoadState state, DateTimeOffset updatedAt)
    {
        LoadState = state;
        Touch(updatedAt);
    }
}
