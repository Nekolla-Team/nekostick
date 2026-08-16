using System.Reflection;
using System.Text;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides stable fixture manifest data without parsing or discovery behavior.</summary>
public static class FixtureManifestData
{
    /// <summary>Gets the stable embedded resource name.</summary>
    public const string ResourceName = "Fixtures.Extension.fixture-manifest.json";

    /// <summary>Gets the stable manifest extension identifier.</summary>
    public const string ExtensionId = "fixture.extension.deterministic";

    /// <summary>Gets the stable manifest version.</summary>
    public const string Version = "1.0.0";

    /// <summary>Reads the named embedded manifest resource as UTF-8 text.</summary>
    /// <returns>The unchanged embedded manifest text.</returns>
    public static string ReadEmbeddedText()
    {
        var assembly = typeof(FixtureManifestData).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The deterministic fixture manifest resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
