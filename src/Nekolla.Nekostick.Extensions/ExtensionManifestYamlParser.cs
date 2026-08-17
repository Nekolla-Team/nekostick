using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Nekolla.Nekostick.Extensions;

internal static class YamlManifestParser
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxManifestDepth = 32;

    internal static ManifestDiscoveryResult Parse(string root, string manifestPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > MaxManifestBytes)
            {
                return Failure(ExtensionFailureCode.YamlInvalid);
            }

            var manifestText = System.Text.Encoding.UTF8.GetString(bytes);
            var prepass = ScanDuplicateScalarKeys(manifestText);
            if (prepass == DuplicateKeyScanResult.Duplicate)
            {
                return Failure(ExtensionFailureCode.DuplicateManifestField);
            }

            if (prepass == DuplicateKeyScanResult.Invalid)
            {
                return Failure(ExtensionFailureCode.YamlInvalid);
            }

            using var reader = new StringReader(manifestText);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return Failure(ExtensionFailureCode.YamlInvalid);
            }

            if (!ValidateShape(mapping, 1, out var shapeFailure))
            {
                return Failure(shapeFailure);
            }

            if (!TryReadMapping(mapping, ManifestSchema.AllowedFields, out var fields, out var fieldFailure))
            {
                return Failure(fieldFailure);
            }

            if (fields.Count != ManifestSchema.AllowedFields.Count)
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid);
            }

            if (!TryReadInt(fields, "schemaVersion", out var schemaVersion) ||
                !TryReadScalar(fields, "id", out var id) ||
                !TryReadScalar(fields, "version", out var version) ||
                !TryReadScalar(fields, "entryAssembly", out var entryAssembly) ||
                !TryReadScalar(fields, "entryType", out var entryType) ||
                !TryReadScalar(fields, "requiredHostApiVersion", out var hostApiVersion) ||
                !fields.TryGetValue("dependencies", out var dependenciesNode) ||
                dependenciesNode is not YamlSequenceNode dependencySequence)
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid);
            }

            var dependencies = new List<ManifestDependencyValues?>();
            foreach (var dependencyNode in dependencySequence.Children)
            {
                if (dependencyNode is not YamlMappingNode dependencyMapping)
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid);
                }

                if (!TryReadMapping(
                        dependencyMapping,
                        ManifestSchema.DependencyFields,
                        out var dependencyFields,
                        out var dependencyFailure))
                {
                    return Failure(dependencyFailure);
                }

                if (dependencyFields.Count != ManifestSchema.DependencyFields.Count ||
                    !TryReadScalar(dependencyFields, "id", out var dependencyId) ||
                    !TryReadScalar(dependencyFields, "versionRange", out var dependencyRange))
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid);
                }

                dependencies.Add(new ManifestDependencyValues(dependencyId, dependencyRange));
            }

            return ManifestParserCore.Validate(
                root,
                ManifestSourceFormat.Yaml,
                new ManifestDocumentValues(
                    schemaVersion,
                    id,
                    version,
                    entryAssembly,
                    entryType,
                    dependencies,
                    hostApiVersion));
        }
        catch (YamlException)
        {
            return Failure(ExtensionFailureCode.YamlInvalid);
        }
        catch (Exception)
        {
            return Failure(ExtensionFailureCode.LoadFailed);
        }
    }

    private enum DuplicateKeyScanResult
    {
        Valid,
        Duplicate,
        Invalid
    }

    private sealed class DuplicateKeyScanScope
    {
        internal DuplicateKeyScanScope(bool isMapping)
        {
            IsMapping = isMapping;
            Keys = isMapping ? new HashSet<string>(StringComparer.Ordinal) : null;
            ExpectingKey = true;
        }

        internal bool IsMapping { get; }

        internal bool ExpectingKey { get; set; }

        internal HashSet<string>? Keys { get; }
    }

    private static DuplicateKeyScanResult ScanDuplicateScalarKeys(string manifestText)
    {
        using var reader = new StringReader(manifestText);
        var parser = new Parser(reader);
        if (!parser.MoveNext() || parser.Current is not StreamStart)
        {
            return DuplicateKeyScanResult.Invalid;
        }

        var scopes = new Stack<DuplicateKeyScanScope>();
        var documentActive = false;
        var rootCompleted = false;
        var streamEnded = false;
        var duplicateFound = false;

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case StreamStart:
                    return DuplicateKeyScanResult.Invalid;
                case StreamEnd:
                    if (streamEnded || documentActive || scopes.Count != 0)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    streamEnded = true;
                    break;
                case DocumentStart:
                    if (streamEnded || documentActive)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    documentActive = true;
                    rootCompleted = false;
                    break;
                case DocumentEnd:
                    if (streamEnded || !documentActive || scopes.Count != 0)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    documentActive = false;
                    break;
                case MappingStart:
                    if (!documentActive || streamEnded || (scopes.Count == 0 && rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    scopes.Push(new DuplicateKeyScanScope(isMapping: true));
                    break;
                case SequenceStart:
                    if (!documentActive || streamEnded || (scopes.Count == 0 && rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    scopes.Push(new DuplicateKeyScanScope(isMapping: false));
                    break;
                case MappingEnd:
                    if (!documentActive || scopes.Count == 0 ||
                        !scopes.Peek().IsMapping || !scopes.Peek().ExpectingKey)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    scopes.Pop();
                    if (!CompleteScannedNode(scopes, ref rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    break;
                case SequenceEnd:
                    if (!documentActive || scopes.Count == 0 || scopes.Peek().IsMapping)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    scopes.Pop();
                    if (!CompleteScannedNode(scopes, ref rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    break;
                case Scalar scalar:
                    if (!documentActive || streamEnded || (scopes.Count == 0 && rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    var isMappingKey = scopes.Count > 0 && scopes.Peek().IsMapping && scopes.Peek().ExpectingKey;
                    if (scalar.IsKey != isMappingKey)
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    if (isMappingKey && !scopes.Peek().Keys!.Add(scalar.Value ?? string.Empty))
                    {
                        duplicateFound = true;
                    }

                    if (!CompleteScannedNode(scopes, ref rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    break;
                case AnchorAlias:
                    if (!documentActive || streamEnded || (scopes.Count == 0 && rootCompleted) ||
                        !CompleteScannedNode(scopes, ref rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    break;
                default:
                    return DuplicateKeyScanResult.Invalid;
            }
        }

        if (!streamEnded || documentActive || scopes.Count != 0)
        {
            return DuplicateKeyScanResult.Invalid;
        }

        return duplicateFound ? DuplicateKeyScanResult.Duplicate : DuplicateKeyScanResult.Valid;
    }

    private static bool CompleteScannedNode(
        Stack<DuplicateKeyScanScope> scopes,
        ref bool rootCompleted)
    {
        if (scopes.Count == 0)
        {
            if (rootCompleted)
            {
                return false;
            }

            rootCompleted = true;
            return true;
        }

        var parent = scopes.Peek();
        if (parent.IsMapping)
        {
            parent.ExpectingKey = !parent.ExpectingKey;
        }

        return true;
    }

    private static bool TryReadMapping(
        YamlMappingNode mapping,
        IReadOnlySet<string> allowed,
        out Dictionary<string, YamlNode> fields,
        out ExtensionFailureCode failure)
    {
        fields = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        failure = ExtensionFailureCode.None;
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode scalarKey || string.IsNullOrEmpty(scalarKey.Value))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            var key = scalarKey.Value!;
            if (!fields.TryAdd(key, pair.Value))
            {
                failure = ExtensionFailureCode.DuplicateManifestField;
                return false;
            }

            if (!allowed.Contains(key))
            {
                failure = ExtensionFailureCode.UnknownManifestField;
                return false;
            }
        }

        return true;
    }

    private static bool TryReadScalar(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        out string? value)
    {
        value = null;
        return fields.TryGetValue(name, out var node) &&
            node is YamlScalarNode scalar &&
            !string.IsNullOrEmpty(scalar.Value) &&
            (value = scalar.Value) is not null;
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        out int? value)
    {
        value = null;
        if (!TryReadScalar(fields, name, out var text) ||
            !int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool ValidateShape(YamlNode node, int depth, out ExtensionFailureCode failure)
    {
        failure = ExtensionFailureCode.None;
        if (depth > MaxManifestDepth)
        {
            failure = ExtensionFailureCode.YamlInvalid;
            return false;
        }

        if (node.GetType().Name.Contains("Alias", StringComparison.Ordinal) ||
            HasUnsafeMetadata(node))
        {
            failure = ExtensionFailureCode.YamlInvalid;
            return false;
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    if (!ValidateShape(pair.Key, depth + 1, out failure) ||
                        !ValidateShape(pair.Value, depth + 1, out failure))
                    {
                        return false;
                    }
                }

                return true;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    if (!ValidateShape(child, depth + 1, out failure))
                    {
                        return false;
                    }
                }

                return true;
            case YamlScalarNode:
                return true;
            default:
                failure = ExtensionFailureCode.YamlInvalid;
                return false;
        }
    }

    private static bool HasUnsafeMetadata(YamlNode node)
    {
        var tag = GetEffectiveTagName(node);
        return !node.Anchor.IsEmpty ||
            (tag.Length > 0 &&
             tag != "!" &&
             !IsAllowedCoreTag(node, tag));
    }

    private static bool IsAllowedCoreTag(YamlNode node, string tag) =>
        node switch
        {
            YamlMappingNode => tag == "tag:yaml.org,2002:map",
            YamlSequenceNode => tag == "tag:yaml.org,2002:seq",
            YamlScalarNode => tag == "tag:yaml.org,2002:null" ||
                tag == "tag:yaml.org,2002:bool" ||
                tag == "tag:yaml.org,2002:int" ||
                tag == "tag:yaml.org,2002:float" ||
                tag == "tag:yaml.org,2002:str",
            _ => false
        };

    private static string GetEffectiveTagName(YamlNode node)
    {
        if (node.Tag.IsEmpty)
        {
            return string.Empty;
        }
        var tag = node.Tag.ToString();
        return tag.StartsWith("!<", StringComparison.Ordinal) &&
            tag.EndsWith('>')
            ? tag[2..^1]
            : tag;
    }

    private static ManifestDiscoveryResult Failure(ExtensionFailureCode code) =>
        ManifestParserCore.Failure(ManifestSourceFormat.Yaml, code);
}
