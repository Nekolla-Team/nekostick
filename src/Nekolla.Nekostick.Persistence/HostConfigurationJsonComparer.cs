using System.Text.Json;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Compares JSONB values without producing a normalized JSON document.</summary>
internal static class HostConfigurationJsonComparer
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 64
    };

    internal static bool AreEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        using var leftDocument = JsonDocument.Parse(left, DocumentOptions);
        using var rightDocument = JsonDocument.Parse(right, DocumentOptions);
        return AreEquivalent(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static bool AreEquivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => AreObjectsEquivalent(left, right),
            JsonValueKind.Array => AreArraysEquivalent(left, right),
            _ => JsonElement.DeepEquals(left, right)
        };
    }

    private static bool AreObjectsEquivalent(JsonElement left, JsonElement right)
    {
        var leftPropertyCount = left.GetPropertyCount();
        if (leftPropertyCount == right.GetPropertyCount())
        {
            if (leftPropertyCount == 0)
            {
                return true;
            }

            if (leftPropertyCount == 1)
            {
                var leftEnumerator = left.EnumerateObject();
                var rightEnumerator = right.EnumerateObject();
                leftEnumerator.MoveNext();
                rightEnumerator.MoveNext();
                var leftProperty = leftEnumerator.Current;
                var rightProperty = rightEnumerator.Current;
                return rightProperty.NameEquals(leftProperty.Name) &&
                    AreEquivalent(leftProperty.Value, rightProperty.Value);
            }
        }

        var leftProperties = GetLastPropertyValues(left);
        var rightProperties = GetLastPropertyValues(right);
        if (leftProperties.Count != rightProperties.Count)
        {
            return false;
        }

        foreach (var property in leftProperties)
        {
            if (!rightProperties.TryGetValue(property.Key, out var rightValue) ||
                !AreEquivalent(property.Value, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, JsonElement> GetLastPropertyValues(JsonElement value)
    {
        var values = new Dictionary<string, JsonElement>(value.GetPropertyCount(), StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            values[property.Name] = property.Value;
        }

        return values;
    }

    private static bool AreArraysEquivalent(JsonElement left, JsonElement right)
    {
        var leftValues = left.EnumerateArray();
        var rightValues = right.EnumerateArray();
        while (leftValues.MoveNext())
        {
            if (!rightValues.MoveNext() || !AreEquivalent(leftValues.Current, rightValues.Current))
            {
                return false;
            }
        }

        return !rightValues.MoveNext();
    }
}
