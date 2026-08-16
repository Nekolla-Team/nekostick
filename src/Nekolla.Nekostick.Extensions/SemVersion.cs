namespace Nekolla.Nekostick.Extensions;

/// <summary>Represents a strict SemVer 2.0.0 value.</summary>
public readonly struct SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private readonly string _prerelease;
    private readonly string _build;

    /// <summary>Creates a semantic version value.</summary>
    /// <param name="major">The non-negative major component.</param>
    /// <param name="minor">The non-negative minor component.</param>
    /// <param name="patch">The non-negative patch component.</param>
    /// <param name="prerelease">Optional SemVer prerelease identifiers.</param>
    /// <param name="build">Optional SemVer build metadata.</param>
    public SemVersion(
        int major,
        int minor,
        int patch,
        string? prerelease = null,
        string? build = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (!SemVersionSyntax.IsValidIdentifiers(
                prerelease,
                allowEmpty: true,
                rejectNumericLeadingZeros: true) ||
            !SemVersionSyntax.IsValidIdentifiers(
                build,
                allowEmpty: true,
                rejectNumericLeadingZeros: false))
        {
            throw new ArgumentException("The semantic version identifiers are invalid.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease ?? string.Empty;
        _build = build ?? string.Empty;
    }

    /// <summary>Gets the major component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch component.</summary>
    public int Patch { get; }

    /// <summary>Gets the prerelease component without its leading hyphen.</summary>
    public string Prerelease => _prerelease;

    /// <summary>Gets the build metadata without its leading plus sign.</summary>
    public string Build => _build;

    /// <summary>Parses a strict SemVer 2.0.0 value.</summary>
    /// <param name="text">The candidate version text.</param>
    /// <param name="version">The parsed value when successful.</param>
    /// <returns><see langword="true" /> when the text is valid SemVer.</returns>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text) || text.Length > 256)
        {
            return false;
        }

        var build = string.Empty;
        var withoutBuild = text;
        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
        {
            if (text.IndexOf('+', plusIndex + 1) >= 0 || plusIndex == text.Length - 1)
            {
                return false;
            }

            build = text[(plusIndex + 1)..];
            withoutBuild = text[..plusIndex];
            if (!SemVersionSyntax.IsValidIdentifiers(
                    build,
                    allowEmpty: false,
                    rejectNumericLeadingZeros: false))
            {
                return false;
            }
        }

        var prerelease = string.Empty;
        var core = withoutBuild;
        var hyphenIndex = withoutBuild.IndexOf('-');
        if (hyphenIndex >= 0)
        {
            if (hyphenIndex == withoutBuild.Length - 1)
            {
                return false;
            }

            prerelease = withoutBuild[(hyphenIndex + 1)..];
            core = withoutBuild[..hyphenIndex];
            if (!SemVersionSyntax.IsValidIdentifiers(
                    prerelease,
                    allowEmpty: false,
                    rejectNumericLeadingZeros: true))
            {
                return false;
            }
        }

        var parts = core.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !SemVersionSyntax.TryParseNumericIdentifier(parts[0], out var major) ||
            !SemVersionSyntax.TryParseNumericIdentifier(parts[1], out var minor) ||
            !SemVersionSyntax.TryParseNumericIdentifier(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, prerelease, build);
        return true;
    }

    /// <summary>Compares semantic version precedence, ignoring build metadata.</summary>
    /// <param name="other">The version to compare.</param>
    /// <returns>A signed comparison result.</returns>
    public int CompareTo(SemVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : ComparePrerelease(_prerelease, other._prerelease);
    }

    /// <summary>Determines whether one version precedes another.</summary>
    public static bool operator <(SemVersion left, SemVersion right) =>
        left.CompareTo(right) < 0;

    /// <summary>Determines whether one version does not exceed another.</summary>
    public static bool operator <=(SemVersion left, SemVersion right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Determines whether one version follows another.</summary>
    public static bool operator >(SemVersion left, SemVersion right) =>
        left.CompareTo(right) > 0;

    /// <summary>Determines whether one version is not below another.</summary>
    public static bool operator >=(SemVersion left, SemVersion right) =>
        left.CompareTo(right) >= 0;

    /// <summary>Compares semantic version precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>The precedence comparison result.</returns>
    public static int Compare(SemVersion left, SemVersion right) => left.CompareTo(right);

    /// <summary>Determines whether two versions have equal SemVer precedence.</summary>
    /// <param name="other">The other version.</param>
    /// <returns><see langword="true" /> when precedence is equal.</returns>
    public bool Equals(SemVersion other) => CompareTo(other) == 0;

    /// <summary>Determines whether two versions have equal SemVer precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true" /> when precedence is equal.</returns>
    public static bool operator ==(SemVersion left, SemVersion right) => left.Equals(right);

    /// <summary>Determines whether two versions have different SemVer precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true" /> when precedence differs.</returns>
    public static bool operator !=(SemVersion left, SemVersion right) => !left.Equals(right);

    /// <summary>Returns a version string.</summary>
    /// <returns>The canonical version text.</returns>
    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (_prerelease.Length > 0)
        {
            value += $"-{_prerelease}";
        }

        return _build.Length > 0 ? $"{value}+{_build}" : value;
    }

    /// <summary>Returns the hash for SemVer precedence.</summary>
    /// <returns>The precedence hash.</returns>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, _prerelease);

    /// <summary>Determines whether an object is an equal semantic version.</summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns><see langword="true" /> when the object is a matching version.</returns>
    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length == 0 ? 0 : 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var leftParts = left.Split('.', StringSplitOptions.None);
        var rightParts = right.Split('.', StringSplitOptions.None);
        var count = Math.Min(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftNumeric = SemVersionSyntax.TryParseNumericIdentifier(leftParts[index], out var leftNumber);
            var rightNumeric = SemVersionSyntax.TryParseNumericIdentifier(rightParts[index], out var rightNumber);
            if (leftNumeric && rightNumeric)
            {
                var numeric = leftNumber.CompareTo(rightNumber);
                if (numeric != 0)
                {
                    return numeric;
                }

                continue;
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            var text = string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (text != 0)
            {
                return text;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}
