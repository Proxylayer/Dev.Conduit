using System.Text.Json;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Certificates;

/// <summary><c>client\imports.json</c>: which server each import came from, and which routes it added.</summary>
public static class ProxyConfigImportRecords
{
    /// <summary>The records, empty when the file is missing or not valid JSON. Reading needs no administrator rights.</summary>
    /// <exception cref="IOException">The file exists but cannot be read.</exception>
    public static List<ProxyConfigImportRecord> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProxyConfigImportRecord>>(File.ReadAllText(path), StorageJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Write(string path, IReadOnlyList<ProxyConfigImportRecord> records) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(records, StorageJson.Options));
}
