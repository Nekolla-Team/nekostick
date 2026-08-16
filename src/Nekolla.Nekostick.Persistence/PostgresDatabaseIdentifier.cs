namespace Nekolla.Nekostick.Persistence;

/// <summary>Validates and quotes controlled PostgreSQL schema identifiers.</summary>
internal static class PostgresDatabaseIdentifier
{
    private const int PostgreSqlIdentifierMaxBytes = 63;

    internal static bool IsValidSchemaIdentifier(string schema)
    {
        if (schema.Length == 0 || schema.Length > PostgreSqlIdentifierMaxBytes)
        {
            return false;
        }

        if (!IsLowerAsciiLetter(schema[0]) && schema[0] != '_')
        {
            return false;
        }

        return schema.Skip(1).All(character =>
            IsLowerAsciiLetter(character) || character is >= '0' and <= '9' || character == '_');
    }

    internal static string QuoteIdentifier(string identifier) =>
        "\u0022" + identifier + "\u0022";

    private static bool IsLowerAsciiLetter(char character) => character is >= 'a' and <= 'z';
}
