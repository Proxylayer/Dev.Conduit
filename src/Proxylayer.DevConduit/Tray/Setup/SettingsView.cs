using System.Text;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// What the setup window sees of one settings file: the configuration below it, the file
/// itself, and the two merged, which is what a launch reading up to this file would get
/// before environment variables and the command line.
/// </summary>
public sealed class SettingsView
{
    private readonly Func<string, string?> _sourceOf;
    private readonly Func<JsonObject, IConfiguration> _withDocument;

    internal SettingsView(
        SettingsTarget target,
        IConfiguration below,
        JsonObject document,
        Func<string, string?> sourceOf,
        Func<JsonObject, IConfiguration> withDocument)
    {
        Target = target;
        Below = below;
        Document = document;
        _sourceOf = sourceOf;
        _withDocument = withDocument;
        Override = ConfigurationOf(document);
        Effective = withDocument(document);
    }

    public SettingsTarget Target { get; }

    /// <summary>Every layer below the file, merged.</summary>
    public IConfiguration Below { get; }

    /// <summary>The file as it is now.</summary>
    public JsonObject Document { get; }

    /// <summary>The file alone, as configuration.</summary>
    public IConfiguration Override { get; }

    /// <summary>The layers below with the file on top.</summary>
    public IConfiguration Effective { get; }

    /// <summary>The name of the highest layer below the file that defines <paramref name="key"/>, or null.</summary>
    public string? SourceOf(string key) => _sourceOf(key);

    /// <summary>The configuration a launch would see once <paramref name="changes"/> were saved to the file.</summary>
    public IConfiguration EffectiveWith(SettingsChangeSet changes)
    {
        var document = (JsonObject)Document.DeepClone();
        changes.ApplyTo(document);
        return _withDocument(document);
    }

    internal static IConfiguration ConfigurationOf(JsonObject document) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString())))
            .Build();
}
