using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// What the tray's "Export client configuration" starts from: the server's host name, client
/// address and Entra ids as the machine settings give them, the file name to offer, and the
/// checks made before the elevated helper is asked to export, so a server that cannot export
/// says so before the elevation prompt. Pure.
/// </summary>
/// <param name="TenantId">The <c>AzureAd:TenantId</c> clients will sign in with; blank or a placeholder when not set.</param>
/// <param name="ClientId">The <c>AzureAd:ClientId</c>; blank or a placeholder when not set.</param>
public sealed record ClientConfigExportPlan(string Hostname, string ClientAddress, string TenantId = "", string ClientId = "")
{
    /// <summary>Shown once the file is written.</summary>
    public const string SecretNote = "This file contains a client certificate. Share it only with people who should use this server.";

    public static ClientConfigExportPlan From(IConfiguration machineConfiguration)
    {
        ArgumentNullException.ThrowIfNull(machineConfiguration);
        var server = machineConfiguration.GetSection(ServerHostOptions.SectionName).Get<ServerHostOptions>() ?? new ServerHostOptions();
        return new ClientConfigExportPlan(
            server.IsManaged ? server.NormalizedHostname : string.Empty,
            server.ClientAddress.Trim(),
            machineConfiguration["AzureAd:TenantId"]?.Trim() ?? string.Empty,
            machineConfiguration["AzureAd:ClientId"]?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Why nothing can be exported yet, or null: the host name and the Entra tenant and client
    /// ids must all be set, the ids not to placeholders, as the export itself requires.
    /// </summary>
    public string? Problem
    {
        get
        {
            var missing = new List<string>();
            if (Hostname.Length == 0)
            {
                missing.Add("the host name");
            }

            if (StartupDiagnostics.IsPlaceholder(TenantId))
            {
                missing.Add("the Entra tenant id");
            }

            if (StartupDiagnostics.IsPlaceholder(ClientId))
            {
                missing.Add("the Entra client id");
            }

            if (missing.Count > 0)
            {
                return $"Fill in {Join(missing)} on the Server page and save, then export again.";
            }

            return !DnsHostname.IsValid(Hostname) ? $"The host name '{Hostname}' is not a DNS host name." : null;
        }
    }

    /// <summary><c>&lt;hostname&gt;.conduit</c>.</summary>
    public string FileName => ProxyConfigDocument.FileNameFor(Hostname);

    /// <summary>Why <paramref name="address"/> cannot go into an export, or null.</summary>
    public static string? AddressProblem(string? address) =>
        string.IsNullOrWhiteSpace(address) ? "Enter the IP address (or name) clients map the host name to."
        : !DnsHostname.IsAddressOrHostname(address) ? $"'{address.Trim()}' is not an IP address or host name."
        : null;

    /// <summary>Why <paramref name="path"/> cannot be handed to the elevated helper, or null.</summary>
    public static string? PathProblem(string? path) =>
        string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? "Choose where to save the file."
        : !path.EndsWith(ProxyConfigDocument.FileExtension, StringComparison.OrdinalIgnoreCase) ? $"The file name must end in {ProxyConfigDocument.FileExtension}."
        : !Directory.Exists(Path.GetDirectoryName(path)) ? "The folder does not exist."
        : Directory.Exists(path) ? "A folder has that name; choose a file name."
        : null;

    private static string Join(List<string> parts) =>
        parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts[..^1])} and {parts[^1]}"
        };
}
