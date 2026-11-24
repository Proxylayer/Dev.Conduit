using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proxylayer.DevConduit.Services;

/// <summary>Serializer settings shared by everything that touches the on-disk store.</summary>
public static class StorageJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
