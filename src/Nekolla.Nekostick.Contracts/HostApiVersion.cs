namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies the semantic version of the stable host API.</summary>
public readonly record struct HostApiVersion : IComparable<HostApiVersion>
{
    /// <summary>Creates a host API semantic version.</summary>
    /// <param name="major">The incompatible API generation.</param>
    /// <param name="minor">The backward-compatible feature version.</param>
    /// <param name="patch">The backward-compatible fix version.</param>
    public HostApiVersion(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>Gets the current host API version.</summary>
    public static HostApiVersion Current { get; } = new(1, 3, 0);

    /// <summary>Gets the major component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch component.</summary>
    public int Patch { get; }

    /// <summary>Compares this version with another semantic version.</summary>
    /// <param name="other">The version to compare.</param>
    /// <returns>A value less than, equal to, or greater than zero.</returns>
    public int CompareTo(HostApiVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Determines whether one host API version precedes another.</summary>
    public static bool operator <(HostApiVersion left, HostApiVersion right) =>
        left.CompareTo(right) < 0;

    /// <summary>Determines whether one host API version does not exceed another.</summary>
    public static bool operator <=(HostApiVersion left, HostApiVersion right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Determines whether one host API version follows another.</summary>
    public static bool operator >(HostApiVersion left, HostApiVersion right) =>
        left.CompareTo(right) > 0;

    /// <summary>Determines whether one host API version is not below another.</summary>
    public static bool operator >=(HostApiVersion left, HostApiVersion right) =>
        left.CompareTo(right) >= 0;

    /// <summary>Returns the semantic version text.</summary>
    /// <returns>The version in major.minor.patch form.</returns>
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
