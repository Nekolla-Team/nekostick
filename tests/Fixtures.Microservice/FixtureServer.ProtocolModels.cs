namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed class RequestReadResult
{
    private RequestReadResult(HttpRequest? request, int? errorStatusCode)
    {
        Request = request;
        ErrorStatusCode = errorStatusCode;
    }

    internal HttpRequest? Request { get; }

    internal int? ErrorStatusCode { get; }

    internal static RequestReadResult Success(HttpRequest request) => new(request, null);

    internal static RequestReadResult Error(int statusCode) => new(null, statusCode);
}

internal sealed class HeaderCollection
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _values.Count;

    internal bool TryAdd(string name, string value) => _values.TryAdd(name, value);

    internal bool Contains(string name) => _values.ContainsKey(name);

    internal string? Get(string name) => _values.GetValueOrDefault(name);

    internal bool HasToken(string name, string token)
    {
        var value = Get(name);
        return value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase)) == true;
    }
}

internal readonly record struct QueryClassification(
    bool Present,
    int ParameterCount,
    bool HasEmptyParameter,
    bool HasKeylessParameter,
    bool HasEmptyValue,
    bool HasPercentEncoding)
{
    internal static QueryClassification From(string value, bool present)
    {
        if (!present)
        {
            return default;
        }

        if (value.Length == 0)
        {
            return new QueryClassification(true, 0, false, false, false, false);
        }

        var segments = value.Split('&');
        var parameterCount = 0;
        var hasEmptyParameter = false;
        var hasKeylessParameter = false;
        var hasEmptyValue = false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                hasEmptyParameter = true;
                continue;
            }

            parameterCount++;
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                hasKeylessParameter = true;
            }
            else if (equals == segment.Length - 1)
            {
                hasEmptyValue = true;
            }
        }

        return new QueryClassification(
            true,
            parameterCount,
            hasEmptyParameter,
            hasKeylessParameter,
            hasEmptyValue,
            value.Contains('%'));
    }
}
