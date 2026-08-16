namespace Nekolla.Nekostick.Contracts;

internal static class IdentityValidation
{
    public static Guid RequireUuidV7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || !IsUuidV7WithRfc4122Variant(value))
        {
            throw new ArgumentException(
                "A UUID v7 identifier with the RFC 4122 variant is required.",
                parameterName);
        }

        return value;
    }

    private static bool IsUuidV7WithRfc4122Variant(Guid value)
    {
        var text = value.ToString("D");
        var variant = text[19];
        return text[14] == '7' &&
            (variant == '8' || variant == '9' || variant == 'a' || variant == 'b');
    }
}
