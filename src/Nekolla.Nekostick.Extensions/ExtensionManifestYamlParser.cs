using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Nekolla.Nekostick.Extensions;

internal static class YamlManifestParser
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxManifestDepth = 32;

    private enum DuplicateKeyScanResult
    {
        Valid,
        Duplicate,
        Invalid
    }

    private sealed class DuplicateKeyScanScope
    {
        internal DuplicateKeyScanScope(bool isMapping, string path)
        {
            IsMapping = isMapping;
            Path = path;
            Keys = isMapping ? new HashSet<string>(StringComparer.Ordinal) : null;
            ExpectingKey = true;
        }

        internal bool IsMapping { get; }

        internal string Path { get; }

        internal bool ExpectingKey { get; set; }

        internal HashSet<string>? Keys { get; }

        internal string? PendingKey { get; set; }

        internal int NextIndex { get; set; }
    }

    internal static ManifestDiscoveryResult Parse(string root, string manifestPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > MaxManifestBytes)
            {
                return Failure(
                    ExtensionFailureCode.YamlInvalid,
                    $"YAML manifest {ExtensionDiagnosticText.Value(manifestPath)} is {bytes.Length} bytes; the maximum supported size is {MaxManifestBytes} bytes.");
            }

            var manifestText = System.Text.Encoding.UTF8.GetString(bytes);
            var prepass = ScanDuplicateScalarKeys(
                manifestText,
                out var duplicatePath,
                out var duplicateField,
                out var duplicateLine);
            if (prepass == DuplicateKeyScanResult.Duplicate)
            {
                return Failure(
                    ExtensionFailureCode.DuplicateManifestField,
                    $"Duplicate manifest field {ExtensionDiagnosticText.Value(duplicateField)} at '{duplicatePath}' on line {duplicateLine}.");
            }

            using var reader = new StringReader(manifestText);
            var stream = new YamlStream();
            stream.Load(reader);
            var rootNode = stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
            if (stream.Documents.Count != 1 || rootNode is not YamlMappingNode mapping)
            {
                return Failure(
                    ExtensionFailureCode.YamlInvalid,
                    $"YAML manifest must contain exactly one mapping document; found {stream.Documents.Count} document(s) with root value {DescribeNode(rootNode)}.");
            }

            if (!ValidateShape(mapping, 1, "root", out var shapeFailure, out var shapeFailureMessage))
            {
                return Failure(shapeFailure, shapeFailureMessage);
            }

            if (!TryReadMapping(
                    mapping,
                    ManifestSchema.AllowedFields,
                    "root",
                    out var fields,
                    out var fieldFailure,
                    out var fieldFailureMessage))
            {
                return Failure(fieldFailure, fieldFailureMessage);
            }

            if (!TryReadRequiredFields(fields, ManifestSchema.RequiredFields, "root", out var missingMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, missingMessage);
            }

            if (!TryReadInt(fields, "schemaVersion", "root", out var schemaVersion, out var failureMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
            }

            if (!TryReadScalar(fields, "id", "root", out var id, out failureMessage) ||
                !TryReadScalar(fields, "version", "root", out var version, out failureMessage) ||
                !TryReadScalar(fields, "entryAssembly", "root", out var entryAssembly, out failureMessage) ||
                !TryReadScalar(fields, "entryType", "root", out var entryType, out failureMessage) ||
                !TryReadScalar(fields, "requiredHostApiVersion", "root", out var hostApiVersion, out failureMessage))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
            }

            if (!fields.TryGetValue("dependencies", out var dependenciesNode))
            {
                return Failure(ExtensionFailureCode.ManifestSchemaInvalid, "Required field 'dependencies' at root is missing.");
            }

            if (dependenciesNode is not YamlSequenceNode dependencySequence)
            {
                return Failure(
                    ExtensionFailureCode.ManifestSchemaInvalid,
                    $"Field 'root.dependencies' value {DescribeNode(dependenciesNode)} must be a YAML sequence.");
            }

            var dependencies = new List<ManifestDependencyValues?>();
            var dependencyIndex = 0;
            foreach (var dependencyNode in dependencySequence.Children)
            {
                var path = $"dependencies[{dependencyIndex}]";
                dependencyIndex++;
                if (dependencyNode is not YamlMappingNode dependencyMapping)
                {
                    return Failure(
                        ExtensionFailureCode.ManifestSchemaInvalid,
                        $"Field '{path}' value {DescribeNode(dependencyNode)} must be a dependency mapping.");
                }

                if (!TryReadMapping(
                        dependencyMapping,
                        ManifestSchema.DependencyFields,
                        path,
                        out var dependencyFields,
                        out var dependencyFailure,
                        out var dependencyFailureMessage))
                {
                    return Failure(dependencyFailure, dependencyFailureMessage);
                }

                if (!TryReadRequiredFields(
                        dependencyFields,
                        ManifestSchema.DependencyRequiredFields,
                        path,
                        out missingMessage))
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid, missingMessage);
                }

                if (!TryReadScalar(dependencyFields, "id", path, out var dependencyId, out failureMessage) ||
                    !TryReadScalar(dependencyFields, "versionRange", path, out var dependencyRange, out failureMessage) ||
                    !TryReadOptionalBool(dependencyFields, "optional", path, out var dependencyOptional, out failureMessage))
                {
                    return Failure(ExtensionFailureCode.ManifestSchemaInvalid, failureMessage);
                }

                dependencies.Add(new ManifestDependencyValues(dependencyId, dependencyRange, dependencyOptional));
            }

            var exportsValid = TryReadExports(fields, out var exports, out var exportFailure, out var exportFailureMessage);
            var importsValid = TryReadImports(fields, out var imports, out var importFailure, out var importFailureMessage);
            if (!exportsValid || !importsValid)
            {
                return Failure(
                    exportFailure != ExtensionFailureCode.None ? exportFailure : importFailure,
                    exportsValid ? importFailureMessage : exportFailureMessage);
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
                    hostApiVersion,
                    exports,
                    imports));
        }
        catch (YamlException exception)
        {
            return Failure(
                ExtensionFailureCode.YamlInvalid,
                $"YAML manifest syntax is invalid ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
        }
        catch (Exception exception)
        {
            return Failure(
                ExtensionFailureCode.LoadFailed,
                $"Could not read YAML manifest {ExtensionDiagnosticText.Value(manifestPath)} ({exception.GetType().Name}): {ExtensionDiagnosticText.Value(exception.Message)}.");
        }
    }

    private static DuplicateKeyScanResult ScanDuplicateScalarKeys(
        string manifestText,
        out string? duplicatePath,
        out string? duplicateField,
        out int duplicateLine)
    {
        duplicatePath = null;
        duplicateField = null;
        duplicateLine = 0;
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

                    scopes.Push(new DuplicateKeyScanScope(isMapping: true, path: GetNextScannedNodePath(scopes)));
                    break;
                case SequenceStart:
                    if (!documentActive || streamEnded || (scopes.Count == 0 && rootCompleted))
                    {
                        return DuplicateKeyScanResult.Invalid;
                    }

                    scopes.Push(new DuplicateKeyScanScope(isMapping: false, path: GetNextScannedNodePath(scopes)));
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

                    if (isMappingKey)
                    {
                        var scope = scopes.Peek();
                        var key = scalar.Value ?? string.Empty;
                        scope.PendingKey = key;
                        if (!scope.Keys!.Add(key) && duplicatePath is null)
                        {
                            duplicatePath = AppendFieldPath(scope.Path, key);
                            duplicateField = key;
                            duplicateLine = checked((int)(scalar.Start.Line + 1));
                        }
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

        return duplicatePath is null ? DuplicateKeyScanResult.Valid : DuplicateKeyScanResult.Duplicate;
    }

    private static string GetNextScannedNodePath(Stack<DuplicateKeyScanScope> scopes)
    {
        if (scopes.Count == 0)
        {
            return "root";
        }

        var parent = scopes.Peek();
        if (!parent.IsMapping)
        {
            return $"{parent.Path}[{parent.NextIndex}]";
        }

        return parent.ExpectingKey
            ? $"{parent.Path}.<key>"
            : AppendFieldPath(parent.Path, parent.PendingKey ?? string.Empty);
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
            if (parent.ExpectingKey)
            {
                parent.PendingKey = null;
            }
        }
        else
        {
            parent.NextIndex++;
        }

        return true;
    }

    private static bool TryReadMapping(
        YamlMappingNode mapping,
        IReadOnlySet<string> allowed,
        string path,
        out Dictionary<string, YamlNode> fields,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        fields = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode scalarKey || string.IsNullOrEmpty(scalarKey.Value))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                failureMessage = $"YAML mapping at '{path}' contains field key value {DescribeNode(pair.Key)}; field names must be non-empty strings.";
                return false;
            }

            var key = scalarKey.Value!;
            if (!fields.TryAdd(key, pair.Value))
            {
                failure = ExtensionFailureCode.DuplicateManifestField;
                failureMessage =
                    $"Duplicate manifest field {ExtensionDiagnosticText.Value(key)} at '{path}' with value {DescribeNode(pair.Value)}.";
                return false;
            }

            if (!allowed.Contains(key))
            {
                failure = ExtensionFailureCode.UnknownManifestField;
                failureMessage = path == "root"
                    ? $"Unknown root field {ExtensionDiagnosticText.Value(key)} has value {DescribeNode(pair.Value)}."
                    : $"Unknown field {ExtensionDiagnosticText.Value(key)} at '{path}' has value {DescribeNode(pair.Value)}.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRequiredFields(
        Dictionary<string, YamlNode> fields,
        IReadOnlySet<string> requiredFields,
        string path,
        out string failureMessage)
    {
        foreach (var requiredField in requiredFields)
        {
            if (!fields.ContainsKey(requiredField))
            {
                failureMessage = $"Required field {ExtensionDiagnosticText.Value(requiredField)} at '{path}' is missing.";
                return false;
            }
        }

        failureMessage = string.Empty;
        return true;
    }

    private static bool TryReadScalar(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string path,
        out string value,
        out string failureMessage)
    {
        value = string.Empty;
        if (!fields.TryGetValue(name, out var node))
        {
            failureMessage = $"Required field '{path}.{name}' is missing.";
            return false;
        }

        if (node is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value))
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeNode(node)} must be a non-empty YAML scalar string.";
            return false;
        }

        value = scalar.Value;
        failureMessage = string.Empty;
        return true;
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string path,
        out int value,
        out string failureMessage)
    {
        value = 0;
        if (!TryReadScalar(fields, name, path, out var text, out failureMessage))
        {
            return false;
        }

        if (!int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value))
        {
            failureMessage = $"Field '{path}.{name}' value {ExtensionDiagnosticText.Value(text)} must be a non-negative 32-bit integer.";
            return false;
        }

        failureMessage = string.Empty;
        return true;
    }

    private static bool TryReadOptionalBool(
        Dictionary<string, YamlNode> fields,
        string name,
        string path,
        out bool value,
        out string failureMessage)
    {
        value = false;
        if (!fields.TryGetValue(name, out var node))
        {
            failureMessage = string.Empty;
            return true;
        }

        if (node is not YamlScalarNode scalar ||
            (!string.Equals(scalar.Value, "true", StringComparison.Ordinal) &&
             !string.Equals(scalar.Value, "false", StringComparison.Ordinal)))
        {
            failureMessage = $"Field '{path}.{name}' value {DescribeNode(node)} must be the YAML boolean 'true' or 'false'.";
            return false;
        }

        value = string.Equals(scalar.Value, "true", StringComparison.Ordinal);
        failureMessage = string.Empty;
        return true;
    }

    private static bool TryReadExports(
        Dictionary<string, YamlNode> fields,
        out List<ManifestContractExportValues> exports,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        exports = new List<ManifestContractExportValues>();
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        if (!fields.TryGetValue("exports", out var node))
        {
            return true;
        }

        if (node is not YamlSequenceNode sequence)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            failureMessage = $"Field 'root.exports' value {DescribeNode(node)} must be a YAML sequence.";
            return false;
        }

        var index = 0;
        foreach (var child in sequence.Children)
        {
            var path = $"exports[{index}]";
            index++;
            if (child is not YamlMappingNode mapping)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                failureMessage = $"Field '{path}' value {DescribeNode(child)} must be an export mapping.";
                return false;
            }

            if (!TryReadMapping(mapping, ManifestSchema.ExportFields, path, out var declaration, out failure, out failureMessage))
            {
                return false;
            }

            if (!TryReadRequiredFields(declaration, ManifestSchema.ExportFields, path, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            if (!TryReadScalar(declaration, "contractId", path, out var id, out failureMessage) ||
                !TryReadScalar(declaration, "version", path, out var version, out failureMessage) ||
                !TryReadScalar(declaration, "assemblyIdentity", path, out var assemblyIdentity, out failureMessage) ||
                !TryReadScalar(declaration, "typeIdentity", path, out var typeIdentity, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            exports.Add(new ManifestContractExportValues(id, version, assemblyIdentity, typeIdentity));
        }

        return true;
    }

    private static bool TryReadImports(
        Dictionary<string, YamlNode> fields,
        out List<ManifestContractImportValues> imports,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        imports = new List<ManifestContractImportValues>();
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        if (!fields.TryGetValue("imports", out var node))
        {
            return true;
        }

        if (node is not YamlSequenceNode sequence)
        {
            failure = ExtensionFailureCode.ManifestSchemaInvalid;
            failureMessage = $"Field 'root.imports' value {DescribeNode(node)} must be a YAML sequence.";
            return false;
        }

        var index = 0;
        foreach (var child in sequence.Children)
        {
            var path = $"imports[{index}]";
            index++;
            if (child is not YamlMappingNode mapping)
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                failureMessage = $"Field '{path}' value {DescribeNode(child)} must be an import mapping.";
                return false;
            }

            if (!TryReadMapping(mapping, ManifestSchema.ImportFields, path, out var declaration, out failure, out failureMessage))
            {
                return false;
            }

            if (!TryReadRequiredFields(declaration, ManifestSchema.ImportRequiredFields, path, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            if (!TryReadScalar(declaration, "contractId", path, out var id, out failureMessage) ||
                !TryReadScalar(declaration, "versionRange", path, out var versionRange, out failureMessage) ||
                !TryReadScalar(declaration, "assemblyIdentity", path, out var assemblyIdentity, out failureMessage) ||
                !TryReadScalar(declaration, "typeIdentity", path, out var typeIdentity, out failureMessage) ||
                !TryReadOptionalBool(declaration, "optional", path, out var importOptional, out failureMessage))
            {
                failure = ExtensionFailureCode.ManifestSchemaInvalid;
                return false;
            }

            imports.Add(new ManifestContractImportValues(id, versionRange, assemblyIdentity, typeIdentity, importOptional));
        }

        return true;
    }

    private static bool ValidateShape(
        YamlNode node,
        int depth,
        string path,
        out ExtensionFailureCode failure,
        out string failureMessage)
    {
        failure = ExtensionFailureCode.None;
        failureMessage = string.Empty;
        if (depth > MaxManifestDepth)
        {
            failure = ExtensionFailureCode.YamlInvalid;
            failureMessage = $"YAML field '{path}' exceeds maximum nesting depth {MaxManifestDepth}.";
            return false;
        }

        if (node.GetType().Name.Contains("Alias", StringComparison.Ordinal) || HasUnsafeMetadata(node))
        {
            failure = ExtensionFailureCode.YamlInvalid;
            failureMessage = $"YAML field '{path}' value {DescribeNode(node)} uses an alias, anchor, or unsupported tag.";
            return false;
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    var valuePath = pair.Key is YamlScalarNode scalarKey && scalarKey.Value is not null
                        ? AppendFieldPath(path, scalarKey.Value)
                        : $"{path}.<value>";
                    if (!ValidateShape(pair.Key, depth + 1, $"{path}.<key>", out failure, out failureMessage) ||
                        !ValidateShape(pair.Value, depth + 1, valuePath, out failure, out failureMessage))
                    {
                        return false;
                    }
                }

                return true;
            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    if (!ValidateShape(sequence.Children[index], depth + 1, $"{path}[{index}]", out failure, out failureMessage))
                    {
                        return false;
                    }
                }

                return true;
            case YamlScalarNode:
                return true;
            default:
                failure = ExtensionFailureCode.YamlInvalid;
                failureMessage = $"YAML field '{path}' has unsupported value {DescribeNode(node)}.";
                return false;
        }
    }

    private static string AppendFieldPath(string path, string field) =>
        $"{path}.{field}";

    private static string DescribeNode(YamlNode? node) =>
        node switch
        {
            null => "<missing>",
            YamlScalarNode scalar => ExtensionDiagnosticText.Value(scalar.Value),
            YamlMappingNode => "<mapping>",
            YamlSequenceNode => "<sequence>",
            _ => $"<{node.GetType().Name}>"
        };

    private static bool HasUnsafeMetadata(YamlNode node)
    {
        var tag = GetEffectiveTagName(node);
        return !node.Anchor.IsEmpty ||
            (tag.Length > 0 && tag != "!" && !IsAllowedCoreTag(node, tag));
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
        return tag.StartsWith("!<", StringComparison.Ordinal) && tag.EndsWith('>')
            ? tag[2..^1]
            : tag;
    }

    private static ManifestDiscoveryResult Failure(ExtensionFailureCode code, string message) =>
        ManifestParserCore.Failure(ManifestSourceFormat.Yaml, code, message);
}
