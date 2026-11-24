using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// The rules the setup window follows when it turns an edited value into a change to a
/// settings file. A value equal to what the layers below already give is removed from the
/// file, so a later appsettings.json carries through; anything else is written.
/// </summary>
/// <remarks>
/// Configuration merges a list from two files entry by entry, by index, so a shorter list in
/// the upper file would leave the lower file's extra entries showing through. A list written
/// over an inherited one is therefore padded with blank entries to the inherited length;
/// every list the window writes drops blank entries when it is read.
/// </remarks>
public static class SettingValues
{
    /// <summary>A text value: blank means inherit, and so does a value equal to the inherited one.</summary>
    public static void Text(SettingsChangeSet changes, string key, string? value, string? inherited)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(trimmed, inherited?.Trim(), StringComparison.Ordinal))
        {
            changes.Remove(key);
        }
        else
        {
            changes.Set(key, trimmed);
        }
    }

    /// <summary>A switch: written only when it differs from the inherited value.</summary>
    public static void Switch(SettingsChangeSet changes, string key, bool value, bool inherited)
    {
        if (value == inherited)
        {
            changes.Remove(key);
        }
        else
        {
            changes.Set(key, value);
        }
    }

    /// <summary>A number: written only when it differs from the inherited value.</summary>
    public static void Number(SettingsChangeSet changes, string key, int value, int inherited)
    {
        if (value == inherited)
        {
            changes.Remove(key);
        }
        else
        {
            changes.Set(key, value);
        }
    }

    /// <summary>A list: empty means inherit, and so does a list equal to the inherited one.</summary>
    public static void List(SettingsChangeSet changes, string key, IReadOnlyList<string> values, IConfiguration below)
    {
        var inherited = below.GetSection(key);
        if (values.Count == 0 || SameList(values, ReadList(inherited)))
        {
            changes.Remove(key);
        }
        else
        {
            changes.Set(key, ListNode(values, inherited));
        }
    }

    /// <summary>
    /// The JSON for a list written over <paramref name="inherited"/>: padded to its length
    /// when it is a list, a single string when it is a single string, so that the merged
    /// configuration holds exactly <paramref name="values"/>.
    /// </summary>
    public static JsonNode ListNode(IReadOnlyList<string> values, IConfigurationSection inherited)
    {
        var inheritedEntries = inherited.GetChildren().Count();

        if (inheritedEntries == 0 && inherited.Value is not null && values.Count <= 1)
        {
            return JsonValue.Create(values.Count == 0 ? string.Empty : values[0]);
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        for (var index = values.Count; index < inheritedEntries; index++)
        {
            array.Add(string.Empty);
        }

        return array;
    }

    /// <summary>A list as the routing parser reads one: its non-blank entries, or its own value.</summary>
    public static IReadOnlyList<string> ReadList(IConfigurationSection section)
    {
        var values = section.GetChildren()
            .Select(child => child.Value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();

        if (values.Count == 0 && section.Value?.Trim() is { Length: > 0 } single)
        {
            values.Add(single);
        }

        return values;
    }

    /// <summary>The non-blank entries of text split on commas and line breaks.</summary>
    public static IReadOnlyList<string> Split(string? text) =>
        (text ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static bool SameList(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets a property, replacing one whose name differs only in case.</summary>
    public static void SetProperty(JsonObject target, string name, JsonNode? value)
    {
        var existing = target.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing != name)
        {
            target.Remove(existing);
        }

        target[name] = value;
    }

    /// <summary>Removes a property whatever its case.</summary>
    public static void RemoveProperty(JsonObject target, string name)
    {
        foreach (var key in target.Select(property => property.Key).Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            target.Remove(key);
        }
    }

    /// <summary>The object at a configuration path in a settings document, or null.</summary>
    public static JsonObject? Find(JsonObject document, string path)
    {
        JsonNode? current = document;
        foreach (var segment in path.Split(':'))
        {
            if (current is not JsonObject node)
            {
                return null;
            }

            current = node.FirstOrDefault(property => string.Equals(property.Key, segment, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return current as JsonObject;
    }
}
