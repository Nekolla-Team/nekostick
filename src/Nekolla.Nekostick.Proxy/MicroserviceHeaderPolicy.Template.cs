using System.Collections.Immutable;
using System.Text;

namespace Nekolla.Nekostick.Proxy;

internal sealed class CompiledHeaderRewriteTemplate
{
    private readonly ImmutableArray<Part> _parts;

    private CompiledHeaderRewriteTemplate(ImmutableArray<Part> parts) => _parts = parts;

    internal static bool TryCompile(
        string? value,
        out CompiledHeaderRewriteTemplate? template)
    {
        template = null;
        if (value is null)
        {
            return false;
        }

        var parts = ImmutableArray.CreateBuilder<Part>();
        var literalStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character < 0x20 || character > 0x7e || character == '\0')
            {
                return false;
            }

            if (character == '}')
            {
                return false;
            }

            if (character != '{')
            {
                continue;
            }

            if (index > literalStart)
            {
                parts.Add(Part.FromLiteral(value[literalStart..index]));
            }

            var close = value.IndexOf('}', index + 1);
            if (close < 0 || !TryParseToken(value[index..(close + 1)], out var token))
            {
                return false;
            }

            parts.Add(Part.FromToken(token));
            index = close;
            literalStart = close + 1;
        }

        if (literalStart < value.Length)
        {
            parts.Add(Part.FromLiteral(value[literalStart..]));
        }

        template = new CompiledHeaderRewriteTemplate(parts.ToImmutable());
        return true;
    }

    internal string Expand(RequestHeaderExpansionContext context)
    {
        var builder = new StringBuilder();
        foreach (var part in _parts)
        {
            if (part.Literal is not null)
            {
                builder.Append(part.Literal);
                continue;
            }

            builder.Append(part.Token switch
            {
                Token.ClientIp => context.ClientIp,
                Token.Path => context.Path,
                Token.Method => context.Method,
                Token.Host => context.Host,
                _ => string.Empty
            });
        }

        return builder.ToString();
    }

    private static bool TryParseToken(string value, out Token token)
    {
        token = value switch
        {
            "{clientIp}" => Token.ClientIp,
            "{path}" => Token.Path,
            "{method}" => Token.Method,
            "{host}" => Token.Host,
            _ => default
        };

        return value is "{clientIp}" or "{path}" or "{method}" or "{host}";
    }

    private enum Token
    {
        ClientIp,
        Path,
        Method,
        Host
    }

    private readonly record struct Part(string? Literal, Token? Token)
    {
        internal static Part FromLiteral(string value) => new(value, null);

        internal static Part FromToken(Token value) => new(null, value);
    }
}
