using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// The environment variables the service control manager gives one service, held in the
/// <c>Environment</c> REG_MULTI_SZ value of the service's registry key. A service sees the
/// machine environment as it was at boot, so a variable set after boot reaches it only
/// through here.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceEnvironment
{
    /// <summary>The variable the host reads the environment name from.</summary>
    public const string EnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string ValueName = "Environment";

    /// <summary>
    /// Sets <paramref name="name"/> to <paramref name="value"/> for the service, keeping any
    /// other variables already configured for it.
    /// </summary>
    public static void Set(string serviceName, string name, string value)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}", writable: true)
            ?? throw new InvalidOperationException($"The service '{serviceName}' has no registry key under HKLM\\{ServicesKey}.");

        var existing = key.GetValue(ValueName) as string[] ?? [];
        key.SetValue(ValueName, Merge(existing, name, value), RegistryValueKind.MultiString);
    }

    /// <summary>
    /// The value the service is given for <paramref name="name"/>, or null when the service
    /// or the variable is absent. Reads only.
    /// </summary>
    public static string? Get(string serviceName, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}", writable: false);
        return Find(key?.GetValue(ValueName) as string[] ?? [], name);
    }

    /// <summary>The value of the last assignment to <paramref name="name"/> in the entries, or null.</summary>
    public static string? Find(IEnumerable<string> entries, string name)
    {
        var prefix = name + "=";
        return entries.LastOrDefault(entry => entry?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)?[prefix.Length..];
    }

    /// <summary>
    /// The entries with any assignment to <paramref name="name"/> replaced by
    /// <paramref name="name"/>=<paramref name="value"/>. Names compare case-insensitively,
    /// as Windows environment variable names do.
    /// </summary>
    public static string[] Merge(IEnumerable<string> entries, string name, string value)
    {
        var prefix = name + "=";
        return
        [
            .. entries.Where(entry => !string.IsNullOrEmpty(entry)
                && !entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
            prefix + value
        ];
    }
}
