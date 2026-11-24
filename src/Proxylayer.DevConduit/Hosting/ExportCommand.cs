using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// <c>Dev.Conduit.exe export [--out &lt;path&gt;] [--address &lt;ip-or-name&gt;] [--Key value ...]</c>:
/// writes <c>&lt;hostname&gt;.conduit</c> for a client. Server role only, with
/// <c>DevConduit:Server:Hostname</c> and the AzureAd ids configured. Issues a new client
/// certificate each time. The certificate folder under the data root is for administrators,
/// so this runs elevated, or as the tray's <c>service-admin export</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExportCommand
{
    public const string Verb = "export";
    public const string OutOption = "--out";
    public const string AddressOption = "--address";

    public static int Run(string[] args)
    {
        if (!CommandArguments.TryTake(args, [OutOption, AddressOption], out var options, out var rest, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            return (int)ServiceAdminExitCode.InvalidArguments;
        }

        using var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(console => console.SingleLine = true));
        return (int)Export(
            options.GetValueOrDefault(OutOption),
            options.GetValueOrDefault(AddressOption),
            rest,
            requireQualifiedOutput: false,
            SettingsFiles.ForThisComputer(),
            loggers,
            Console.Out);
    }

    /// <summary>The export itself, for the command line and the elevated helper alike.</summary>
    /// <param name="requireQualifiedOutput">
    /// True for the elevated helper: the output must be a fully qualified <c>.conduit</c>
    /// path in an existing folder, never a default and never anything else.
    /// </param>
    public static ServiceAdminExitCode Export(
        string? outputPath,
        string? address,
        string[] configurationArguments,
        bool requireQualifiedOutput,
        SettingsFiles settingsFiles,
        ILoggerFactory? loggers,
        TextWriter output)
    {
        loggers ??= NullLoggerFactory.Instance;

        if (!CommandConfiguration.TryBuild(configurationArguments, settingsFiles, out var configuration, out var configurationError))
        {
            output.WriteLine(configurationError);
            return ServiceAdminExitCode.ExportRefused;
        }

        if (!DevConduitModeSelection.TryResolve(configuration, configurationArguments, out var mode, out var modeError) || mode != DevConduitMode.Server)
        {
            output.WriteLine(modeError.Length > 0 ? modeError : "Export runs on a server; this machine is configured for local mode.");
            return ServiceAdminExitCode.ExportRefused;
        }

        var devConduit = configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>() ?? new DevConduitOptions();
        var server = devConduit.Server;
        var refusal = Refusal(configuration, server, ref address);
        if (refusal is not null)
        {
            output.WriteLine(refusal);
            return ServiceAdminExitCode.ExportRefused;
        }

        var hostname = server.NormalizedHostname;
        var path = ResolveOutputPath(outputPath, hostname, requireQualifiedOutput);
        if (path is null)
        {
            output.WriteLine($"{OutOption} must name a {ProxyConfigDocument.FileExtension} file in an existing folder, not a folder or a link.");
            return ServiceAdminExitCode.InvalidArguments;
        }

        var scope = (configuration.GetSection(ApiScopeOptions.SectionName).Get<ApiScopeOptions>() ?? new ApiScopeOptions()).Primary;
        if (!ProxyConfigValidator.IsScopeName(scope))
        {
            output.WriteLine($"AzureAd:Scopes starts with '{scope}', which is not a plain scope name such as {ApiScopeOptions.DefaultScope}. Set the scope's own name, not its api:// URI.");
            return ServiceAdminExitCode.ExportRefused;
        }

        var routes = ExportableRoutes(configuration, output);
        var authority = new ServerCertificateAuthority(
            ServerCertificateStore.ForDataRoot(devConduit.ResolveDataRoot()),
            TimeProvider.System,
            loggers.CreateLogger("Dev.Conduit.Certificates"));

        try
        {
            var result = new ProxyConfigExporter(authority, TimeProvider.System).Export(
                new ProxyConfigExportRequest(
                    hostname,
                    address!,
                    server.Port,
                    configuration["AzureAd:TenantId"]!,
                    configuration["AzureAd:ClientId"]!,
                    routes,
                    scope),
                path);

            output.WriteLine($"Wrote {result.Path}.");
            output.WriteLine($"  Server {hostname}:{server.Port}, clients map it to {result.Document.Address}; remote '{result.Document.Remote}'.");
            output.WriteLine($"  {result.Document.ServerCertificates.Count} server certificate(s); {routes.Count} route(s); scope {scope}.");
            output.WriteLine($"  Enrollment {result.ClientCertificate.Enrollment}: client certificate {result.ClientCertificate.Serial}, valid until {result.ClientCertificate.NotAfter:yyyy-MM-dd}.");
            output.WriteLine("  The file holds a client certificate's private key: hand it over as a secret. Revoke the enrollment,");
            output.WriteLine("  with every renewal made from it, with");
            output.WriteLine($"  Dev.Conduit.exe certs revoke {result.ClientCertificate.Serial}");
            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
                                              or System.Security.Cryptography.CryptographicException or InvalidOperationException
                                              or System.Text.Json.JsonException)
        {
            output.WriteLine($"The export failed: {exception.Message}");
            return ServiceAdminExitCode.ExportFailed;
        }
    }

    /// <summary>Why this server cannot export, or null; fills in the address from configuration when none was given.</summary>
    private static string? Refusal(IConfiguration configuration, ServerHostOptions server, ref string? address)
    {
        if (!server.IsManaged)
        {
            return "DevConduit:Server:Hostname is not set. Set the server's host name in its settings first.";
        }

        if (server.Problem() is { } problem)
        {
            return problem;
        }

        if (ManagedHttpsEndpoint.IsOverridden(configuration))
        {
            return "Kestrel:Endpoints is configured, so the server does not serve its app-managed certificate and a .conduit would pin the wrong one. Remove Kestrel:Endpoints first.";
        }

        if (StartupDiagnostics.IsPlaceholder(configuration["AzureAd:TenantId"]) || StartupDiagnostics.IsPlaceholder(configuration["AzureAd:ClientId"]))
        {
            return "AzureAd:TenantId and AzureAd:ClientId must be set: clients sign in with them.";
        }

        address = string.IsNullOrWhiteSpace(address) ? server.ClientAddress : address;
        if (string.IsNullOrWhiteSpace(address))
        {
            return $"No client address: give {AddressOption} <ip-or-name>, or set DevConduit:Server:ClientAddress.";
        }

        if (!DnsHostname.IsAddressOrHostname(address))
        {
            return $"'{address}' is not an IP address or host name.";
        }

        address = address.Trim();
        return null;
    }

    /// <summary>
    /// The file to write, or null when it is not one this command writes. Checked before any
    /// client certificate is issued: the name must end in <c>.conduit</c>, its folder must
    /// exist, and it must not be a folder or a link itself. From the command line a folder, or
    /// nothing, names the folder the default file name goes into.
    /// </summary>
    private static string? ResolveOutputPath(string? outputPath, string hostname, bool requireQualified)
    {
        string path;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            if (requireQualified)
            {
                return null;
            }

            path = Path.Combine(Directory.GetCurrentDirectory(), ProxyConfigDocument.FileNameFor(hostname));
        }
        else
        {
            if (requireQualified && !Path.IsPathFullyQualified(outputPath))
            {
                return null;
            }

            path = Path.GetFullPath(outputPath);
            if (Directory.Exists(path) && !requireQualified)
            {
                path = Path.Combine(path, ProxyConfigDocument.FileNameFor(hostname));
            }
        }

        if (!path.EndsWith(ProxyConfigDocument.FileExtension, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(path) is not { } folder
            || !Directory.Exists(folder)
            || Directory.Exists(path))
        {
            return null;
        }

        var existing = new FileInfo(path);
        return existing.Exists && existing.Attributes.HasFlag(FileAttributes.ReparsePoint) ? null : path;
    }

    /// <summary>The server's usable routes, name to origin, as its alias table reads them.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ExportableRoutes(IConfiguration configuration, TextWriter output)
    {
        var routing = RoutingConfigurationParser.Parse(
            configuration.GetSection(DevConduitOptions.SectionName),
            configuration.GetSection("AzureAd"));
        var aliases = new RouteAliasTable(routing);

        var routes = new List<KeyValuePair<string, string>>();
        foreach (var name in aliases.Names)
        {
            if (!ProxyConfigValidator.IsName(name))
            {
                output.WriteLine($"  Route '{name}' is not exported: its name is not one a client can configure.");
                continue;
            }

            aliases.TryResolve(name, out var origin);
            routes.Add(new KeyValuePair<string, string>(name, origin!.GetLeftPart(UriPartial.Authority)));
        }

        return routes;
    }
}
