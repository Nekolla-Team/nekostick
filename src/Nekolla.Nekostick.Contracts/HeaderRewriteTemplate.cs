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

/// <summary>Identifies why a header rewrite template could not be compiled.</summary>
public enum HeaderRewriteTemplateCompileFailureCode
{
    /// <summary>The template value was null.</summary>
    MissingTemplate,

    /// <summary>The template contains a control or non-ASCII character.</summary>
    InvalidCharacter,

    /// <summary>The template contains an unmatched closing brace.</summary>
    UnexpectedClosingBrace,

    /// <summary>A token opening brace has no matching closing brace.</summary>
    UnclosedToken,

    /// <summary>The template contains a token name that is not supported.</summary>
    UnsupportedToken
}

/// <summary>Contains an immutable, validated header rewrite template.</summary>
public sealed class HeaderRewriteTemplate
{
    private readonly ImmutableArray<Part> _parts;

    private HeaderRewriteTemplate(ImmutableArray<Part> parts) => _parts = parts;

    /// <summary>Compiles a literal-and-token header rewrite template.</summary>
    /// <param name="value">The candidate set/add value. Empty and space-only values are valid literal templates.</param>
    /// <returns>The compiled template on success, or a failure code with required precise detail.</returns>
    public static HeaderRewriteTemplateCompileResult TryCompile(string? value)
    {
        if (value is null)
        {
            return HeaderRewriteTemplateCompileResult.Failure(
                HeaderRewriteTemplateCompileFailureCode.MissingTemplate,
                new ExtensionErrorDetail("A header rewrite template is required."));
        }

        var parts = ImmutableArray.CreateBuilder<Part>();
        var literalStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character < 0x20 || character > 0x7e || character == '\0')
            {
                return HeaderRewriteTemplateCompileResult.Failure(
                    HeaderRewriteTemplateCompileFailureCode.InvalidCharacter,
                    new ExtensionErrorDetail($"The header rewrite template contains a control or non-ASCII character at zero-based position {index}."));
            }

            if (character == '}')
            {
                return HeaderRewriteTemplateCompileResult.Failure(
                    HeaderRewriteTemplateCompileFailureCode.UnexpectedClosingBrace,
                    new ExtensionErrorDetail($"The header rewrite template contains an unmatched closing brace at zero-based position {index}."));
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
            if (close < 0)
            {
                return HeaderRewriteTemplateCompileResult.Failure(
                    HeaderRewriteTemplateCompileFailureCode.UnclosedToken,
                    new ExtensionErrorDetail($"The header rewrite token starting at zero-based position {index} has no closing brace."));
            }

            if (!TryParseToken(value[index..(close + 1)], out var token))
            {
                return HeaderRewriteTemplateCompileResult.Failure(
                    HeaderRewriteTemplateCompileFailureCode.UnsupportedToken,
                    new ExtensionErrorDetail($"The header rewrite token at zero-based position {index} is not supported."));
            }

            parts.Add(Part.FromToken(token));
            index = close;
            literalStart = close + 1;
        }

        if (literalStart < value.Length)
        {
            parts.Add(Part.FromLiteral(value[literalStart..]));
        }

        return HeaderRewriteTemplateCompileResult.Success(new HeaderRewriteTemplate(parts.ToImmutable()));
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

/// <summary>Contains the outcome of compiling a header rewrite template.</summary>
/// <remarks>A success carries the compiled template; a failure carries its reason and required precise detail.</remarks>
public abstract class HeaderRewriteTemplateCompileResult
{
    private protected HeaderRewriteTemplateCompileResult()
    {
    }

    /// <summary>Gets whether the template compiled successfully.</summary>
    public abstract bool Succeeded { get; }

    /// <summary>Creates a success result carrying the compiled template.</summary>
    /// <param name="template">The compiled immutable header rewrite template.</param>
    /// <returns>A success subtype carrying <paramref name="template" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template" /> is <see langword="null" />.</exception>
    public static HeaderRewriteTemplateCompileResult Success(HeaderRewriteTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new HeaderRewriteTemplateCompileSuccessResult(template);
    }

    /// <summary>Creates a failure result carrying the reason and required precise detail.</summary>
    /// <param name="code">The reason the template could not be compiled.</param>
    /// <param name="detail">The required precise cause or additional context.</param>
    /// <returns>A failure subtype carrying <paramref name="code" /> and <paramref name="detail" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detail" /> is <see langword="null" />.</exception>
    public static HeaderRewriteTemplateCompileResult Failure(
        HeaderRewriteTemplateCompileFailureCode code,
        ExtensionErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new HeaderRewriteTemplateCompileFailureResult(code, detail);
    }
}

/// <summary>Contains a successfully compiled header rewrite template.</summary>
public sealed class HeaderRewriteTemplateCompileSuccessResult : HeaderRewriteTemplateCompileResult
{
    internal HeaderRewriteTemplateCompileSuccessResult(HeaderRewriteTemplate template)
    {
        Template = template;
    }

    /// <inheritdoc />
    public override bool Succeeded => true;

    /// <summary>Gets the compiled immutable header rewrite template.</summary>
    public HeaderRewriteTemplate Template { get; }
}

/// <summary>Contains the reason and required detail for a failed header rewrite template compilation.</summary>
public sealed class HeaderRewriteTemplateCompileFailureResult : HeaderRewriteTemplateCompileResult
{
    internal HeaderRewriteTemplateCompileFailureResult(
        HeaderRewriteTemplateCompileFailureCode code,
        ExtensionErrorDetail detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <inheritdoc />
    public override bool Succeeded => false;

    /// <summary>Gets the reason the template could not be compiled.</summary>
    public HeaderRewriteTemplateCompileFailureCode Code { get; }

    /// <summary>Gets the required precise failure cause or additional context.</summary>
    public ExtensionErrorDetail Detail { get; }
}
