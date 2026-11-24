using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Changes to a settings file, keyed by configuration path (<c>DevConduit:Local:Port</c>): values
/// to set, each replacing whatever the path held, including a whole section, and paths to
/// remove. Applying it touches nothing else in the document. Its JSON form is what the setup
/// window hands to the elevated helper to apply to the machine settings file.
/// </summary>
public sealed class SettingsChangeSet
{
    private const string SetProperty = "set";
    private const string RemoveProperty = "remove";

    private readonly Dictionary<string, JsonNode> _set = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _remove = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths given a value, with the value each is set to.</summary>
    public IReadOnlyDictionary<string, JsonNode> Values => _set;

    /// <summary>The paths removed.</summary>
    public IReadOnlyCollection<string> Removed => _remove;

    /// <summary>Every path this change set touches.</summary>
    public IEnumerable<string> Keys => _set.Keys.Concat(_remove);

    public bool IsEmpty => _set.Count == 0 && _remove.Count == 0;

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, replacing an earlier change to the same key.</summary>
    public SettingsChangeSet Set(string key, JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = ValidateKey(key);
        _remove.Remove(path);
        _set[path] = value.DeepClone();
        return this;
    }

    public SettingsChangeSet Set(string key, string value) => Set(key, JsonValue.Create(value));

    public SettingsChangeSet Set(string key, bool value) => Set(key, JsonValue.Create(value));

    public SettingsChangeSet Set(string key, int value) => Set(key, JsonValue.Create(value));

    /// <summary>Removes <paramref name="key"/>, replacing an earlier change to the same key.</summary>
    public SettingsChangeSet Remove(string key)
    {
        var path = ValidateKey(key);
        _set.Remove(path);
        _remove.Add(path);
        return this;
    }

    /// <summary>
    /// Applies the changes to <paramref name="root"/>. Property names match ignoring case, as
    /// configuration keys do. A removal also drops any object it leaves empty.
    /// </summary>
    public void ApplyTo(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var key in _remove)
        {
            RemovePath(root, key.Split(':'));
        }

        foreach (var (key, value) in _set)
        {
            var segments = key.Split(':');
            var parent = root;
            foreach (var segment in segments[..^1])
            {
                var existingName = FindName(parent, segment);
                if (existingName is not null && parent[existingName] is JsonObject child)
                {
                    parent = child;
                    continue;
                }

                // A value where an object is needed is replaced: the change set is authoritative.
                var created = new JsonObject(parent.Options);
                parent[existingName ?? segment] = created;
                parent = created;
            }

            var leaf = segments[^1];
            parent[FindName(parent, leaf) ?? leaf] = value.DeepClone();
        }
    }

    /// <summary>The JSON form, <c>{ "set": { key: value }, "remove": [ key ] }</c>.</summary>
    public string ToJson()
    {
        var set = new JsonObject();
        foreach (var (key, value) in _set)
        {
            set[key] = value.DeepClone();
        }

        var remove = new JsonArray();
        foreach (var key in _remove)
        {
            remove.Add(key);
        }

        return new JsonObject { [SetProperty] = set, [RemoveProperty] = remove }.ToJsonString(SettingsJson.WriteOptions);
    }

    /// <summary>Reads the JSON form written by <see cref="ToJson"/>, or says why it cannot.</summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out SettingsChangeSet? changes, out string error)
    {
        changes = null;
        error = string.Empty;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: SettingsJson.DocumentOptions);
        }
        catch (JsonException exception)
        {
            error = $"The change set is not valid JSON: {exception.Message}";
            return false;
        }

        if (root is not JsonObject document)
        {
            error = "The change set must be a JSON object.";
            return false;
        }

        var result = new SettingsChangeSet();
        try
        {
            foreach (var (name, node) in document)
            {
                switch (name)
                {
                    case SetProperty when node is JsonObject values:
                        foreach (var (key, value) in values)
                        {
                            if (value is null)
                            {
                                error = $"The change set gives '{key}' no value.";
                                return false;
                            }

                            result.Set(key, value);
                        }

                        break;

                    case RemoveProperty when node is JsonArray keys:
                        foreach (var key in keys)
                        {
                            if (key is not JsonValue value || !value.TryGetValue<string>(out var text))
                            {
                                error = "Every entry under 'remove' must be a string.";
                                return false;
                            }

                            result.Remove(text);
                        }

                        break;

                    default:
                        error = $"The change set has an unexpected property '{name}'.";
                        return false;
                }
            }
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        changes = result;
        return true;
    }

    private static void RemovePath(JsonObject root, string[] segments)
    {
        var chain = new List<(JsonObject Parent, string Name)>();
        var current = root;

        for (var index = 0; index < segments.Length; index++)
        {
            var name = FindName(current, segments[index]);
            if (name is null)
            {
                return;
            }

            chain.Add((current, name));

            if (index < segments.Length - 1)
            {
                if (current[name] is not JsonObject next)
                {
                    return;
                }

                current = next;
            }
        }

        var (leafParent, leafName) = chain[^1];
        leafParent.Remove(leafName);

        for (var index = chain.Count - 2; index >= 0; index--)
        {
            var (parent, name) = chain[index];
            if (parent[name] is JsonObject { Count: 0 })
            {
                parent.Remove(name);
            }
            else
            {
                break;
            }
        }
    }

    private static string? FindName(JsonObject parent, string segment) =>
        parent.Select(property => property.Key).FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));

    private static string ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A settings key cannot be blank.", nameof(key));
        }

        var trimmed = key.Trim();
        if (trimmed.Split(':').Any(segment => segment.Trim().Length == 0 || segment != segment.Trim()))
        {
            throw new ArgumentException($"'{key}' is not a settings key: every part between colons must be a name.", nameof(key));
        }

        return trimmed;
    }
}
