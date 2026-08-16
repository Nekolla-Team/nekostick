using System.Collections.Immutable;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Represents a small deterministic SemVer range expression.</summary>
public sealed class SemVersionRange
{
    private static readonly char[] RangeSeparators = [' ', '\t', '\r', '\n', ','];
    private readonly ImmutableArray<ImmutableArray<VersionComparator>> _alternatives;

    private SemVersionRange(string expression, ImmutableArray<ImmutableArray<VersionComparator>> alternatives)
    {
        Expression = expression;
        _alternatives = alternatives;
    }

    /// <summary>Gets the normalized source expression.</summary>
    public string Expression { get; }

    /// <summary>Parses a supported SemVer range expression.</summary>
    /// <param name="text">The range expression.</param>
    /// <param name="range">The parsed range when successful.</param>
    /// <returns><see langword="true" /> when the expression is supported and valid.</returns>
    public static bool TryParse(string? text, out SemVersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512)
        {
            return false;
        }

        var expression = text.Trim();
        var alternatives = ImmutableArray.CreateBuilder<ImmutableArray<VersionComparator>>();
        var alternativeTexts = expression.Split("||", StringSplitOptions.None);
        foreach (var alternativeText in alternativeTexts)
        {
            var tokens = alternativeText.Split(RangeSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            var comparators = ImmutableArray.CreateBuilder<VersionComparator>();
            foreach (var token in tokens)
            {
                if (!TryParseToken(token, comparators))
                {
                    return false;
                }
            }

            alternatives.Add(comparators.ToImmutable());
        }

        range = new SemVersionRange(expression, alternatives.ToImmutable());
        return true;
    }

    /// <summary>Tests a version against this range.</summary>
    /// <param name="version">The candidate version.</param>
    /// <returns><see langword="true" /> when any alternative fully matches.</returns>
    public bool IsSatisfiedBy(SemVersion version)
    {
        foreach (var alternative in _alternatives)
        {
            var matches = true;
            foreach (var comparator in alternative)
            {
                if (!comparator.Matches(version))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the original range expression.</summary>
    /// <returns>The range text.</returns>
    public override string ToString() => Expression;

    private static bool TryParseToken(string token, ImmutableArray<VersionComparator>.Builder comparators)
    {
        if (token is "*" or "x" or "X")
        {
            return true;
        }

        if (token[0] is '^' or '~')
        {
            if (!SemVersion.TryParse(token[1..], out var version))
            {
                return false;
            }

            comparators.Add(new VersionComparator(
                VersionComparison.GreaterThanOrEqual,
                version));
            if (!TryGetUpperBound(token[0], version, out var upperBound))
            {
                return false;
            }

            comparators.Add(new VersionComparator(VersionComparison.LessThan, upperBound));
            return true;
        }

        var operatorLength = token.StartsWith(">=", StringComparison.Ordinal) ||
            token.StartsWith("<=", StringComparison.Ordinal)
            ? 2
            : token[0] is '>' or '<' or '='
                ? 1
                : 0;
        var operatorText = operatorLength == 0 ? string.Empty : token[..operatorLength];
        var operand = token[operatorLength..];
        if (TryParseWildcard(operand, out var wildcardComparators))
        {
            if (operatorLength != 0)
            {
                return false;
            }

            comparators.AddRange(wildcardComparators);
            return true;
        }

        if (!SemVersion.TryParse(operand, out var exactVersion))
        {
            return false;
        }

        var comparison = operatorText switch
        {
            ">" => VersionComparison.GreaterThan,
            ">=" => VersionComparison.GreaterThanOrEqual,
            "<" => VersionComparison.LessThan,
            "<=" => VersionComparison.LessThanOrEqual,
            "" or "=" => VersionComparison.Equal,
            _ => VersionComparison.Invalid
        };
        if (comparison == VersionComparison.Invalid)
        {
            return false;
        }

        comparators.Add(new VersionComparator(comparison, exactVersion));
        return true;
    }

    private static bool TryParseWildcard(
        string text,
        out ImmutableArray<VersionComparator> comparators)
    {
        comparators = ImmutableArray<VersionComparator>.Empty;
        var parts = text.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var wildcardIndex = -1;
        var values = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index] is "x" or "X" or "*")
            {
                if (wildcardIndex >= 0 || index == 0 && parts.Length == 1)
                {
                    return false;
                }

                wildcardIndex = index;
                continue;
            }

            if (wildcardIndex >= 0 || !SemVersionSyntax.TryParseNumericIdentifier(parts[index], out values[index]))
            {
                return false;
            }
        }

        if (wildcardIndex < 0)
        {
            return false;
        }

        var lower = new SemVersion(values[0], values[1], values[2]);
        if (!TryGetWildcardUpperBound(wildcardIndex, values, out var upper))
        {
            return false;
        }

        comparators = ImmutableArray.Create(
            new VersionComparator(VersionComparison.GreaterThanOrEqual, lower),
            new VersionComparator(VersionComparison.LessThan, upper));
        return true;
    }

    private static bool TryGetWildcardUpperBound(int wildcardIndex, int[] values, out SemVersion upper)
    {
        upper = default;
        try
        {
            upper = wildcardIndex switch
            {
                0 => new SemVersion(1, 0, 0),
                1 => new SemVersion(values[0] + 1, 0, 0),
                2 => new SemVersion(values[0], values[1] + 1, 0),
                _ => default
            };
            return wildcardIndex is >= 0 and <= 2;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetUpperBound(char operatorCharacter, SemVersion version, out SemVersion upper)
    {
        upper = default;
        try
        {
            if (operatorCharacter == '~')
            {
                upper = new SemVersion(version.Major, checked(version.Minor + 1), 0);
            }
            else if (version.Major > 0)
            {
                upper = new SemVersion(checked(version.Major + 1), 0, 0);
            }
            else if (version.Minor > 0)
            {
                upper = new SemVersion(0, checked(version.Minor + 1), 0);
            }
            else
            {
                upper = new SemVersion(0, 0, checked(version.Patch + 1));
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private enum VersionComparison
    {
        Invalid,
        Equal,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual
    }

    private readonly record struct VersionComparator(VersionComparison Comparison, SemVersion Version)
    {
        internal bool Matches(SemVersion candidate)
        {
            var comparison = candidate.CompareTo(Version);
            return Comparison switch
            {
                VersionComparison.Equal => comparison == 0,
                VersionComparison.GreaterThan => comparison > 0,
                VersionComparison.GreaterThanOrEqual => comparison >= 0,
                VersionComparison.LessThan => comparison < 0,
                VersionComparison.LessThanOrEqual => comparison <= 0,
                _ => false
            };
        }
    }
}
