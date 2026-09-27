using System.Collections.Immutable;
using System.Text;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Reports an invalid or unresolved service launch template.</summary>
public sealed class ServiceTemplateException : Exception
{
    /// <summary>Creates a service template exception without a placeholder token.</summary>
    /// <param name="message">The safe diagnostic message.</param>
    public ServiceTemplateException(string message)
        : this(message, null)
    {
    }

    /// <summary>Creates a service template exception for a placeholder token.</summary>
    /// <param name="message">The safe diagnostic message.</param>
    /// <param name="token">The placeholder token text, when available.</param>
    public ServiceTemplateException(string message, string? token)
        : base(message)
    {
        Token = token;
    }

    /// <summary>Gets the placeholder token text that caused the failure.</summary>
    public string? Token { get; }
}

/// <summary>Provides values and optional resolvers used while expanding service launch templates.</summary>
public sealed class ServiceTemplateContext
{
    /// <summary>Creates a service launch template context.</summary>
    /// <param name="variables">The local variable values.</param>
    /// <param name="remoteResolver">The optional resolver for remote service variables.</param>
    /// <param name="legacyVariables">The optional bare-dollar variable values.</param>
    public ServiceTemplateContext(
        IReadOnlyDictionary<string, string> variables,
        Func<Guid, string, string?>? remoteResolver = null,
        IReadOnlyDictionary<string, string>? legacyVariables = null)
    {
        ArgumentNullException.ThrowIfNull(variables);

        Variables = CopyOrdinal(variables);
        RemoteResolver = remoteResolver;
        LegacyVariables = legacyVariables is null
            ? ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
            : CopyOrdinal(legacyVariables);
    }

    /// <summary>Gets the local variable values.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Gets the optional remote service variable resolver.</summary>
    public Func<Guid, string, string?>? RemoteResolver { get; }

    /// <summary>Gets the bare-dollar legacy variable values.</summary>
    public IReadOnlyDictionary<string, string> LegacyVariables { get; }

    private static ImmutableDictionary<string, string> CopyOrdinal(
        IReadOnlyDictionary<string, string> source)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            builder.Add(entry.Key, entry.Value);
        }

        return builder.ToImmutable();
    }
}

/// <summary>Expands service launch template placeholders.</summary>
public static class ServiceLaunchTemplate
{
    private const int MaximumRecursionDepth = 32;

    /// <summary>Expands placeholders in a service launch template.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="context">The values and resolvers used for expansion.</param>
    /// <returns>The expanded text.</returns>
    public static string Expand(string text, ServiceTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder(text.Length);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        AppendExpanded(builder, text, context, visiting, 0);
        return builder.ToString();
    }

    /// <summary>Expands each argument in an immutable argument collection.</summary>
    /// <param name="arguments">The arguments to expand.</param>
    /// <param name="context">The values and resolvers used for expansion.</param>
    /// <returns>The expanded arguments.</returns>
    public static ImmutableArray<string> ExpandArguments(
        ImmutableArray<string> arguments,
        ServiceTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (arguments.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>(arguments.Length);
        foreach (var argument in arguments)
        {
            builder.Add(Expand(argument, context));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Expands environment values using dynamic launch values.</summary>
    /// <param name="environment">The configured environment entries.</param>
    /// <param name="dynamicValues">The dynamic values that overlay the configured entries.</param>
    /// <param name="remoteResolver">The optional resolver for remote service variables.</param>
    /// <returns>The expanded environment entries.</returns>
    public static IReadOnlyDictionary<string, string> ExpandEnvironment(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> dynamicValues,
        Func<Guid, string, string?>? remoteResolver)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(dynamicValues);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            variables[entry.Key] = entry.Value;
        }

        foreach (var entry in dynamicValues)
        {
            variables[entry.Key] = entry.Value;
        }

        var context = new ServiceTemplateContext(variables, remoteResolver);
        var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            expanded[entry.Key] = Expand(entry.Value, context);
        }

        foreach (var entry in dynamicValues)
        {
            expanded[entry.Key] = entry.Value;
        }

        return expanded;
    }

    /// <summary>Extracts remote service dependencies from template texts.</summary>
    /// <param name="texts">The template texts to scan.</param>
    /// <returns>The distinct referenced service identifiers.</returns>
    public static ImmutableHashSet<Guid> ExtractDependencies(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var dependencies = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            ScanDependencies(text, dependencies);
        }

        return dependencies.ToImmutable();
    }

    private static void AppendExpanded(
        StringBuilder builder,
        string text,
        ServiceTemplateContext context,
        HashSet<string> visiting,
        int depth)
    {
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\\')
            {
                if (index + 1 < text.Length && text[index + 1] == '$')
                {
                    if (IsHostPrefix(text, index + 1))
                    {
                        AppendHostToken(builder, text, index + 1, index, throwOnUnterminated: true);
                        index = FindClosingBrace(text, index + 1) + 1;
                    }
                    else
                    {
                        builder.Append('$');
                        index += 2;
                    }
                }
                else
                {
                    builder.Append('\\');
                    index++;
                }

                continue;
            }

            if (character != '$')
            {
                builder.Append(character);
                index++;
                continue;
            }

            if (index + 1 >= text.Length || text[index + 1] != '{')
            {
                if (!TryAppendLegacy(builder, text, index, context.LegacyVariables, out var consumed))
                {
                    builder.Append('$');
                    consumed = 1;
                }

                index += consumed;
                continue;
            }

            var close = text.IndexOf('}', index + 2);
            if (close < 0)
            {
                throw CreateUnterminatedException(text, index);
            }

            var rawToken = text.Substring(index, close - index + 1);
            if (IsHostPrefix(text, index))
            {
                builder.Append(rawToken);
                index = close + 1;
                continue;
            }

            var expression = text.Substring(index + 2, close - index - 2);
            if (expression.Length == 0)
            {
                throw CreateMalformedException(rawToken);
            }

            var separator = expression.IndexOf('@');
            if (separator >= 0)
            {
                AppendRemoteValue(builder, context, rawToken, expression, separator);
            }
            else
            {
                if (!IsValidName(expression))
                {
                    throw CreateMalformedException(rawToken);
                }

                AppendLocalValue(builder, context, visiting, depth, expression, rawToken);
            }

            index = close + 1;
        }
    }

    private static void AppendLocalValue(
        StringBuilder builder,
        ServiceTemplateContext context,
        HashSet<string> visiting,
        int depth,
        string name,
        string rawToken)
    {
        if (!context.Variables.TryGetValue(name, out var value) || value is null)
        {
            throw new ServiceTemplateException(
                $"The service template variable '{name}' was not found.",
                rawToken);
        }

        if (!visiting.Add(name))
        {
            throw new ServiceTemplateException(
                $"The service template variable '{name}' contains a cycle.",
                rawToken);
        }

        try
        {
            if (depth >= MaximumRecursionDepth)
            {
                throw new ServiceTemplateException(
                    $"The service template expansion exceeded the maximum recursion depth for variable '{name}'.",
                    rawToken);
            }

            AppendExpanded(builder, value, context, visiting, depth + 1);
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private static void AppendRemoteValue(
        StringBuilder builder,
        ServiceTemplateContext context,
        string rawToken,
        string expression,
        int separator)
    {
        var name = expression[..separator].Trim();
        var serviceText = expression[(separator + 1)..].Trim();
        if (!IsValidName(name) || serviceText.Length == 0 ||
            !Guid.TryParse(serviceText, out var serviceId))
        {
            throw CreateMalformedException(rawToken);
        }

        if (context.RemoteResolver is null)
        {
            throw new ServiceTemplateException(
                $"The remote service template variable '{name}' for service '{serviceId}' could not be resolved.",
                rawToken);
        }

        var value = context.RemoteResolver(serviceId, name);
        if (value is null)
        {
            throw new ServiceTemplateException(
                $"The remote service template variable '{name}' for service '{serviceId}' could not be resolved.",
                rawToken);
        }

        builder.Append(value);
    }

    private static bool TryAppendLegacy(
        StringBuilder builder,
        string text,
        int dollarIndex,
        IReadOnlyDictionary<string, string> legacyVariables,
        out int consumed)
    {
        var start = dollarIndex + 1;
        if (start >= text.Length || !IsIdentifierCharacter(text[start]))
        {
            consumed = 0;
            return false;
        }

        var end = start + 1;
        while (end < text.Length && IsIdentifierCharacter(text[end]))
        {
            end++;
        }

        for (var length = end - start; length > 0; length--)
        {
            var candidateEnd = start + length;
            if (candidateEnd < text.Length && IsIdentifierCharacter(text[candidateEnd]))
            {
                continue;
            }

            var candidate = text.Substring(start, length);
            if (legacyVariables.TryGetValue(candidate, out var value))
            {
                builder.Append(value);
                consumed = length + 1;
                return true;
            }
        }

        consumed = 0;
        return false;
    }

    private static void ScanDependencies(
        string text,
        ImmutableHashSet<Guid>.Builder dependencies)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                if (index + 1 < text.Length && text[index + 1] == '$')
                {
                    if (IsHostPrefix(text, index + 1))
                    {
                        var close = text.IndexOf('}', index + 3);
                        if (close < 0)
                        {
                            return;
                        }

                        index = close + 1;
                    }
                    else
                    {
                        index += 2;
                    }
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (text[index] != '$' || index + 1 >= text.Length || text[index + 1] != '{')
            {
                index++;
                continue;
            }

            var closeIndex = text.IndexOf('}', index + 2);
            if (closeIndex < 0)
            {
                return;
            }

            if (!IsHostPrefix(text, index))
            {
                var expression = text.Substring(index + 2, closeIndex - index - 2);
                var separator = expression.IndexOf('@');
                if (separator >= 0 && TryParseRemoteExpression(expression, separator, out _, out var serviceId))
                {
                    dependencies.Add(serviceId);
                }
            }

            index = closeIndex + 1;
        }
    }

    private static bool TryParseRemoteExpression(
        string expression,
        int separator,
        out string name,
        out Guid serviceId)
    {
        name = expression[..separator].Trim();
        var serviceText = expression[(separator + 1)..].Trim();
        serviceId = Guid.Empty;
        return IsValidName(name) && serviceText.Length > 0 && Guid.TryParse(serviceText, out serviceId);
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || !IsNameStart(name[0]))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!IsIdentifierCharacter(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNameStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierCharacter(char character) =>
        IsNameStart(character) || character is >= '0' and <= '9';

    private static bool IsHostPrefix(string text, int dollarIndex) =>
        dollarIndex + 7 <= text.Length && text.AsSpan(dollarIndex, 7).SequenceEqual("${HOST:".AsSpan());

    private static void AppendHostToken(
        StringBuilder builder,
        string text,
        int dollarIndex,
        int slashIndex,
        bool throwOnUnterminated)
    {
        var close = text.IndexOf('}', dollarIndex + 2);
        if (close < 0)
        {
            if (throwOnUnterminated)
            {
                throw CreateUnterminatedException(text, dollarIndex);
            }

            return;
        }

        builder.Append(text, slashIndex, close - slashIndex + 1);
    }

    private static int FindClosingBrace(string text, int dollarIndex) =>
        text.IndexOf('}', dollarIndex + 2);

    private static ServiceTemplateException CreateMalformedException(string rawToken) =>
        new($"The service template token '{rawToken}' is malformed.", rawToken);

    private static ServiceTemplateException CreateUnterminatedException(string text, int dollarIndex)
    {
        var rawToken = text[dollarIndex..];
        return new ServiceTemplateException(
            $"The service template token '{rawToken}' is unterminated.",
            rawToken);
    }
}
