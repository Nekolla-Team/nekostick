using System.Collections.Immutable;
using System.Text;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies one supported request-header rewrite expansion token.</summary>
public enum HeaderRewriteTemplateToken
{
    /// <summary>The trusted client address selected for the request.</summary>
    ClientIp,

    /// <summary>The match-time forwarded path.</summary>
    Path,

    /// <summary>The HTTP request method.</summary>
    Method,

    /// <summary>The safe current request host.</summary>
    Host
}

/// <summary>Contains an immutable, validated header rewrite template.</summary>
public sealed class HeaderRewriteTemplate
{
    private readonly ImmutableArray<Part> _parts;

    private HeaderRewriteTemplate(ImmutableArray<Part> parts) => _parts = parts;

    /// <summary>Compiles a literal-and-token header rewrite template.</summary>
    /// <param name="value">The candidate set/add value.</param>
    /// <param name="template">The immutable compiled template when valid.</param>
    /// <returns>True only when the value contains safe literal text and known tokens.</returns>
    public static bool TryCompile(string? value, out HeaderRewriteTemplate? template)
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

        template = new HeaderRewriteTemplate(parts.ToImmutable());
        return true;
    }

    /// <summary>Expands this validated template with one request-local context.</summary>
    public string Expand(
        string clientIp,
        string path,
        string method,
        string host)
    {
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(host);

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
                HeaderRewriteTemplateToken.ClientIp => clientIp,
                HeaderRewriteTemplateToken.Path => path,
                HeaderRewriteTemplateToken.Method => method,
                HeaderRewriteTemplateToken.Host => host,
                _ => string.Empty
            });
        }

        return builder.ToString();
    }

    private static bool TryParseToken(
        string value,
        out HeaderRewriteTemplateToken token)
    {
        token = value switch
        {
            "{clientIp}" => HeaderRewriteTemplateToken.ClientIp,
            "{path}" => HeaderRewriteTemplateToken.Path,
            "{method}" => HeaderRewriteTemplateToken.Method,
            "{host}" => HeaderRewriteTemplateToken.Host,
            _ => default
        };

        return value is "{clientIp}" or "{path}" or "{method}" or "{host}";
    }

    private readonly record struct Part(
        string? Literal,
        HeaderRewriteTemplateToken? Token)
    {
        internal static Part FromLiteral(string value) => new(value, null);

        internal static Part FromToken(HeaderRewriteTemplateToken value) => new(null, value);
    }
}
