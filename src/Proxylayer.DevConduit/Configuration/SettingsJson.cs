using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// How the settings files are read and written: comments and trailing commas tolerated as the
/// configuration provider tolerates them, property names matched ignoring case as
/// configuration keys are, and written indented without escaping URLs.
/// </summary>
public static class SettingsJson
{
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static JsonNodeOptions NodeOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    public static JsonSerializerOptions WriteOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Parses a settings document. Blank text is an empty document.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidDataException">The text is JSON but not an object.</exception>
    public static JsonObject Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject(NodeOptions);
        }

        return JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("A settings file must hold a single JSON object, { ... }.");
    }
}
