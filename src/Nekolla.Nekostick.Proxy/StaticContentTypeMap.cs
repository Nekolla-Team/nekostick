using System.IO;

namespace Nekolla.Nekostick.Proxy;

internal static class StaticContentTypeMap
{
    public static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return "text/html; charset=utf-8";
        }

        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            return "text/css; charset=utf-8";
        }

        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            return "text/javascript; charset=utf-8";
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json; charset=utf-8";
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml; charset=utf-8";
        }

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain; charset=utf-8";
        }

        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml";
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return "image/gif";
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return "image/x-icon";
        }

        if (extension.Equals(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return "application/wasm";
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "application/pdf";
        }

        return "application/octet-stream";
    }
}
