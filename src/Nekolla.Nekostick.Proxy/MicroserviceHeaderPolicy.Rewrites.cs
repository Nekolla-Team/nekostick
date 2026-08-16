using System.Collections.Immutable;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class MicroserviceHttpTransformer
{
    internal static bool IsHeaderNameSafe(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        const string separators = "()<>@,;:\\\"/[]?={} \t";
        foreach (var character in name)
        {
            if (character <= 0x20 || character >= 0x7f || separators.Contains(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsHeaderValueSafe(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character < 0x20 || character > 0x7e || character == '\0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ApplyRewrites(
        HttpRequestHeaders headers,
        HttpContent? content,
        ImmutableArray<CompiledHeaderRewrite> rewrites,
        RequestHeaderExpansionContext expansionContext)
    {
        foreach (var operation in new[]
        {
            HeaderRewriteOperation.Remove,
            HeaderRewriteOperation.Set,
            HeaderRewriteOperation.Add
        })
        {
            foreach (var rewrite in rewrites)
            {
                if (rewrite.Operation != operation
                    || !IsHeaderNameSafe(rewrite.Name)
                    || IsProtectedHeader(rewrite.Name, rewrite.Operation, requestSide: true)
                    || (operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add
                        && !IsHeaderValueSafe(rewrite.Value)))
                {
                    if (rewrite.Operation == operation)
                    {
                        return false;
                    }

                    continue;
                }

                var expandedValue = rewrite.Expand(expansionContext);
                if (!IsHeaderValueSafe(expandedValue))
                {
                    return false;
                }

                switch (operation)
                {
                    case HeaderRewriteOperation.Remove:
                        RemoveRequestHeader(headers, content?.Headers, rewrite.Name);
                        break;
                    case HeaderRewriteOperation.Set:
                        if (rewrite.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                headers.Host = expandedValue;
                            }
                            catch (FormatException)
                            {
                                return false;
                            }

                            break;
                        }

                        RemoveRequestHeader(headers, content?.Headers, rewrite.Name);
                        if (!TryAddRequestHeader(headers, content, rewrite.Name, expandedValue))
                        {
                            return false;
                        }

                        break;
                    case HeaderRewriteOperation.Add:
                        if (!TryAddRequestHeader(headers, content, rewrite.Name, expandedValue))
                        {
                            return false;
                        }

                        break;
                }
            }
        }

        return true;
    }

    private static bool ApplyResponseRewrites(
        IHeaderDictionary headers,
        ImmutableArray<CompiledHeaderRewrite> rewrites,
        RequestHeaderExpansionContext expansionContext)
    {
        foreach (var operation in new[]
        {
            HeaderRewriteOperation.Remove,
            HeaderRewriteOperation.Set,
            HeaderRewriteOperation.Add
        })
        {
            foreach (var rewrite in rewrites)
            {
                if (rewrite.Operation != operation
                    || !IsHeaderNameSafe(rewrite.Name)
                    || IsProtectedHeader(rewrite.Name, rewrite.Operation, requestSide: false)
                    || (operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add
                        && !IsHeaderValueSafe(rewrite.Value)))
                {
                    if (rewrite.Operation == operation)
                    {
                        return false;
                    }

                    continue;
                }

                var expandedValue = rewrite.Expand(expansionContext);
                if (!IsHeaderValueSafe(expandedValue))
                {
                    return false;
                }

                switch (operation)
                {
                    case HeaderRewriteOperation.Remove:
                        headers.Remove(rewrite.Name);
                        break;
                    case HeaderRewriteOperation.Set:
                        headers.Remove(rewrite.Name);
                        if (!TryAddResponseHeader(headers, rewrite.Name, expandedValue))
                        {
                            return false;
                        }

                        break;
                    case HeaderRewriteOperation.Add:
                        if (!TryAddResponseHeader(headers, rewrite.Name, expandedValue))
                        {
                            return false;
                        }

                        break;
                }
            }
        }

        return true;
    }

    private static bool IsProtectedHeader(
        string name,
        HeaderRewriteOperation operation,
        bool requestSide) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            ? !requestSide || operation != HeaderRewriteOperation.Set
            : name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase)
              || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
              || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase);

    private static void RemoveRequestHeader(
        HttpRequestHeaders headers,
        HttpHeaders? contentHeaders,
        string name)
    {
        if (IsContentHeaderName(name))
        {
            contentHeaders?.Remove(name);
            return;
        }

        headers.Remove(name);
    }

    private static bool TryAddRequestHeader(
        HttpRequestHeaders headers,
        HttpContent? content,
        string name,
        string value)
    {
        if (IsContentHeaderName(name))
        {
            return content?.Headers.TryAddWithoutValidation(name, [value]) == true;
        }

        return headers.TryAddWithoutValidation(name, [value]);
    }

    private static bool IsContentHeaderName(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase);

    private static bool TryAddResponseHeader(
        IHeaderDictionary headers,
        string name,
        string value)
    {
        if (headers.TryGetValue(name, out var current))
        {
            headers[name] = StringValues.Concat(current, new StringValues(value));
        }
        else
        {
            headers[name] = new StringValues(value);
        }

        return true;
    }

}
