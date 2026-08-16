using System.Globalization;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Executes safe GET and HEAD static-file responses without owning HTTP transport.</summary>
public static class StaticHttpExecutor
{
    /// <summary>
    /// Resolves and opens one normalized static request, then creates a response plan.
    /// Query strings are intentionally outside this API and never participate in disk mapping.
    /// </summary>
    public static StaticHttpExecutionResult Execute(
        StaticTargetDefinition target,
        string method,
        string normalizedRequestPath,
        StaticHttpRequestHeaders? requestHeaders = null,
        StaticHttpExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (method is null)
        {
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.UnsupportedMethod);
        }

        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.UnsupportedMethod);
        }

        requestHeaders ??= StaticHttpRequestHeaders.Empty;
        options ??= StaticHttpExecutionOptions.Default;
        if (!AreSafeRequestHeaders(requestHeaders))
        {
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.InvalidRequest);
        }

        var resolution = StaticFileRequestMapper.Map(target, method, normalizedRequestPath);
        if (!resolution.IsOpenable)
        {
            return FailureForResolution(resolution);
        }

        using var openResult = target.OpenRead(resolution);
        if (!openResult.IsOpened)
        {
            return FailureForOpen(openResult);
        }

        var handle = openResult.TransferHandle()!;
        try
        {
            return BuildResponse(
                handle,
                method.Equals("HEAD", StringComparison.OrdinalIgnoreCase),
                requestHeaders,
                options);
        }
        catch (IOException)
        {
            handle.Dispose();
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.TargetChanged);
        }
        catch (ObjectDisposedException)
        {
            handle.Dispose();
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.TargetChanged);
        }
    }

    private static StaticHttpExecutionResult BuildResponse(
        StaticFileReadHandle handle,
        bool headRequest,
        StaticHttpRequestHeaders requestHeaders,
        StaticHttpExecutionOptions options)
    {
        var entityTag = GetResponseEntityTag(handle);
        if (requestHeaders.IfMatch is not null
            && !MatchesEntityTag(requestHeaders.IfMatch, entityTag, strong: true))
        {
            return CreateNoBodyResponse(412, handle, options, contentLength: 0);
        }

        if (requestHeaders.IfNoneMatch is not null
            && MatchesEntityTag(requestHeaders.IfNoneMatch, entityTag, strong: false))
        {
            return CreateNoBodyResponse(304, handle, options, contentLength: null);
        }

        if (requestHeaders.IfNoneMatch is null
            && IsNotModifiedSince(requestHeaders.IfModifiedSince, handle.LastModifiedUtc))
        {
            return CreateNoBodyResponse(304, handle, options, contentLength: null);
        }

        var rangeResult = ParseRange(requestHeaders.Range, handle.Length);
        if (rangeResult.Kind == RangeKind.Multiple)
        {
            handle.Dispose();
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.MultipleRangesNotSupported);
        }

        if (rangeResult.Kind == RangeKind.Invalid)
        {
            handle.Dispose();
            return StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.InvalidRange);
        }

        if (rangeResult.Kind == RangeKind.Unsatisfiable)
        {
            return CreateRangeNotSatisfiableResponse(handle, options);
        }

        if (rangeResult.Kind == RangeKind.Single)
        {
            var range = rangeResult.Range;
            var contentRange = FormattableString.Invariant(
                $"bytes {range.Start}-{range.End}/{handle.Length}");
            var headers = CreateHeaders(
                handle,
                options,
                range.Length,
                contentRange);
            return new StaticHttpExecutionResult(
                StaticHttpExecutionKind.Response,
                new StaticHttpResponsePlan(
                    206,
                    headers,
                    handle,
                    range.Start,
                    headRequest ? 0 : range.Length,
                    hasBody: !headRequest));
        }

        var fullHeaders = CreateHeaders(handle, options, handle.Length, contentRange: null);
        return new StaticHttpExecutionResult(
            StaticHttpExecutionKind.Response,
            new StaticHttpResponsePlan(
                200,
                fullHeaders,
                handle,
                bodyOffset: 0,
                bodyLength: headRequest ? 0 : handle.Length,
                hasBody: !headRequest));
    }

    private static StaticHttpExecutionResult CreateNoBodyResponse(
        int statusCode,
        StaticFileReadHandle handle,
        StaticHttpExecutionOptions options,
        long? contentLength)
    {
        var headers = CreateHeaders(handle, options, contentLength, contentRange: null, includeContentType: false);
        return new StaticHttpExecutionResult(
            StaticHttpExecutionKind.Response,
            new StaticHttpResponsePlan(
                statusCode,
                headers,
                handle,
                bodyOffset: 0,
                bodyLength: 0,
                hasBody: false));
    }

    private static StaticHttpExecutionResult CreateRangeNotSatisfiableResponse(
        StaticFileReadHandle handle,
        StaticHttpExecutionOptions options)
    {
        var contentRange = FormattableString.Invariant($"bytes */{handle.Length}");
        var headers = CreateHeaders(handle, options, contentLength: 0, contentRange: contentRange);
        return new StaticHttpExecutionResult(
            StaticHttpExecutionKind.Response,
            new StaticHttpResponsePlan(
                416,
                headers,
                handle,
                bodyOffset: 0,
                bodyLength: 0,
                hasBody: false));
    }

    private static StaticHttpResponseHeaders CreateHeaders(
        StaticFileReadHandle handle,
        StaticHttpExecutionOptions options,
        long? contentLength,
        string? contentRange,
        bool includeContentType = true) =>
        new(
            includeContentType ? handle.ContentType : null,
            contentLength,
            GetResponseEntityTag(handle),
            handle.LastModifiedUtc,
            contentRange,
            options.CacheControl);

    private static string GetResponseEntityTag(StaticFileReadHandle handle) =>
        handle.ETag.StartsWith("W/", StringComparison.Ordinal)
            ? handle.ETag
            : $"W/{handle.ETag}";

    private static StaticHttpExecutionResult FailureForResolution(StaticFileResolution resolution) =>
        resolution.FailureReason switch
        {
            StaticFileFailureReason.UnsupportedMethod =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.UnsupportedMethod),
            StaticFileFailureReason.InvalidRequestPath =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.InvalidRequest),
            StaticFileFailureReason.TargetNotFound =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.NotFound),
            StaticFileFailureReason.DirectoryIndexMissing or StaticFileFailureReason.DirectoryListingDisabled =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.DirectoryListingDisabled),
            StaticFileFailureReason.AccessDenied =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.AccessDenied),
            StaticFileFailureReason.TargetChanged =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.TargetChanged),
            StaticFileFailureReason.RootUnavailable
                or StaticFileFailureReason.OutsideRoot
                or StaticFileFailureReason.UnsafeFilesystemTarget
                or StaticFileFailureReason.ResolutionNotOwned
                or StaticFileFailureReason.None =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.InvalidMapping),
            _ => StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.Forbidden)
        };

    private static StaticHttpExecutionResult FailureForOpen(StaticFileOpenResult openResult) =>
        openResult.FailureReason switch
        {
            StaticFileFailureReason.TargetNotFound =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.NotFound),
            StaticFileFailureReason.AccessDenied =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.AccessDenied),
            StaticFileFailureReason.TargetChanged =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.TargetChanged),
            StaticFileFailureReason.ResolutionNotOwned or StaticFileFailureReason.InvalidRequestPath =>
                StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.InvalidMapping),
            _ => StaticHttpExecutionResult.Failure(StaticHttpExecutionKind.Forbidden)
        };

    private static bool AreSafeRequestHeaders(StaticHttpRequestHeaders headers) =>
        IsSafeRequestHeader(headers.IfMatch)
        && IsSafeRequestHeader(headers.IfNoneMatch)
        && IsSafeRequestHeader(headers.IfModifiedSince)
        && IsSafeRequestHeader(headers.Range);

    private static bool IsSafeRequestHeader(string? value)
    {
        if (value is null)
        {
            return true;
        }

        foreach (var character in value)
        {
            if (character > 0x7e || character < 0x20 || character == '\0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesEntityTag(string value, string currentTag, bool strong)
    {
        foreach (var candidatePart in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (candidatePart == "*")
            {
                return true;
            }

            if (!TryParseEntityTag(candidatePart, out var candidate, out var weak))
            {
                continue;
            }

            if (strong && weak)
            {
                continue;
            }

            if (strong
                ? string.Equals(candidate, currentTag, StringComparison.Ordinal)
                : string.Equals(StripWeakPrefix(candidate), StripWeakPrefix(currentTag), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseEntityTag(string value, out string tag, out bool weak)
    {
        tag = string.Empty;
        weak = false;
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            weak = true;
            value = value[2..];
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == '"' || value[index] < 0x21 || value[index] > 0x7e)
            {
                return false;
            }
        }

        tag = value;
        return true;
    }

    private static string StripWeakPrefix(string value) =>
        value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;

    private static bool IsNotModifiedSince(string? value, DateTimeOffset lastModifiedUtc)
    {
        if (value is null
            || !DateTimeOffset.TryParseExact(
                value.Trim(),
                "R",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestDate))
        {
            return false;
        }

        var representationDate = new DateTimeOffset(
            lastModifiedUtc.UtcDateTime.AddTicks(-(lastModifiedUtc.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)),
            TimeSpan.Zero);
        return representationDate <= requestDate;
    }

    private static RangeResult ParseRange(string? value, long length)
    {
        if (value is null)
        {
            return RangeResult.None;
        }

        var separator = value.IndexOf('=');
        if (separator <= 0
            || !value[..separator].Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || separator == value.Length - 1)
        {
            return RangeResult.Invalid;
        }

        var ranges = value[(separator + 1)..].Split(',');
        if (ranges.Length != 1)
        {
            return RangeResult.Multiple;
        }

        var rangeValue = ranges[0].Trim();
        var dash = rangeValue.IndexOf('-');
        if (dash < 0 || dash != rangeValue.LastIndexOf('-'))
        {
            return RangeResult.Invalid;
        }

        var startText = rangeValue[..dash].Trim();
        var endText = rangeValue[(dash + 1)..].Trim();
        if (startText.Length == 0 && endText.Length == 0)
        {
            return RangeResult.Invalid;
        }

        if (startText.Length == 0)
        {
            return TryParseNonNegative(endText, out var suffixLength) && suffixLength > 0
                ? CreateSuffixRange(length, suffixLength)
                : RangeResult.Unsatisfiable;
        }

        if (!TryParseNonNegative(startText, out var start))
        {
            return RangeResult.Invalid;
        }

        if (start >= length)
        {
            return RangeResult.Unsatisfiable;
        }

        if (endText.Length == 0)
        {
            return RangeResult.Single(new ByteRange(start, length - 1));
        }

        if (!TryParseNonNegative(endText, out var end))
        {
            return RangeResult.Invalid;
        }

        return end < start
            ? RangeResult.Unsatisfiable
            : RangeResult.Single(new ByteRange(start, Math.Min(end, length - 1)));
    }

    private static RangeResult CreateSuffixRange(long length, long suffixLength)
    {
        if (length == 0)
        {
            return RangeResult.Unsatisfiable;
        }

        var actualLength = Math.Min(length, suffixLength);
        return RangeResult.Single(new ByteRange(length - actualLength, length - 1));
    }

    private static bool TryParseNonNegative(string value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
        && result >= 0;

    private enum RangeKind
    {
        None,
        Single,
        Multiple,
        Invalid,
        Unsatisfiable
    }

    private readonly record struct ByteRange(long Start, long End)
    {
        public long Length => End - Start + 1;
    }

    private readonly struct RangeResult
    {
        private RangeResult(RangeKind kind, ByteRange range)
        {
            Kind = kind;
            Range = range;
        }

        public RangeKind Kind { get; }

        public ByteRange Range { get; }

        public static RangeResult None => new(RangeKind.None, default);

        public static RangeResult Invalid => new(RangeKind.Invalid, default);

        public static RangeResult Multiple => new(RangeKind.Multiple, default);

        public static RangeResult Unsatisfiable => new(RangeKind.Unsatisfiable, default);

        public static RangeResult Single(ByteRange range) => new(RangeKind.Single, range);
    }
}
