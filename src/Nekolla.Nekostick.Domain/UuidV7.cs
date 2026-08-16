namespace Nekolla.Nekostick.Domain;

/// <summary>Abstracts UUID version 7 creation for domain entities.</summary>
public interface IUuidV7Generator
{
    /// <summary>Creates a new UUID version 7.</summary>
    /// <returns>A time-ordered UUID version 7.</returns>
    Guid Create();
}

/// <summary>Uses the .NET UUID version 7 implementation.</summary>
public sealed class SystemUuidV7Generator : IUuidV7Generator
{
    /// <summary>Creates a new UUID version 7.</summary>
    /// <returns>A new UUID version 7.</returns>
    public Guid Create()
    {
        var value = Guid.CreateVersion7();
        return UuidV7.IsVersion7(value)
            ? value
            : throw new InvalidOperationException("The UUID v7 generator returned an invalid identifier.");
    }
}

/// <summary>Provides UUID version checks used by domain boundaries.</summary>
public static class UuidV7
{
    /// <summary>Checks whether a non-empty UUID uses version 7 and the RFC 4122 variant.</summary>
    /// <param name="value">The UUID to inspect.</param>
    /// <returns><see langword="true"/> when the UUID is version 7 with standard RFC variant bits.</returns>
    public static bool IsVersion7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var text = value.ToString("D");
        var variant = text[19];
        return text[14] == '7' &&
            (variant == '8' || variant == '9' || variant == 'a' || variant == 'b');
    }

    internal static Guid RequireVersion7(Guid value, string parameterName)
    {
        if (!IsVersion7(value))
        {
            throw new ArgumentException(
                "A UUID v7 identifier with the RFC 4122 variant is required.",
                parameterName);
        }

        return value;
    }
}
