using System.Net.NetworkInformation;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The server's host name when <c>DevConduit:Server:Hostname</c> names none: this computer's DNS
/// name, its fully qualified name when it has a primary DNS suffix or a domain, else its
/// computer name, lower-cased. It applies by the rule <see cref="DataRootDefault"/> uses: in
/// server mode, when running as the service or while the <c>Dev.Conduit</c> service is
/// installed, so the managed HTTPS endpoint and its certificate come up without configuration.
/// A console run on a machine without the service keeps <c>http://localhost:8091</c>. An
/// explicit host name, or an explicit <c>Kestrel:Endpoints</c>, always wins: with either, no
/// default is added.
/// </summary>
public static class ServerHostnameDefault
{
    public const string Key = ManagedSettingKeys.ServerHostname;

    /// <summary>How the setup window describes a host name that is this default.</summary>
    public const string Hint = "this computer's name — saved only if you change it";

    /// <summary>True when the computer's name is the default host name.</summary>
    public static bool UsesComputerName(DevConduitMode mode, bool runningAsService, bool serviceInstalled) =>
        DataRootDefault.UsesMachineFolder(mode, runningAsService, serviceInstalled);

    /// <summary>This computer's DNS name as a host name, or null when it is not a valid one.</summary>
    public static string? ComputerName()
    {
        string? hostName = null;
        string? domainName = null;
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            hostName = properties.HostName;
            domainName = properties.DomainName;
        }
        catch (NetworkInformationException)
        {
            // The computer name alone is still a name.
        }

        return FromNames(hostName, domainName, Environment.MachineName);
    }

    /// <summary>
    /// The host name for a computer whose DNS host name, primary DNS suffix or domain, and
    /// NetBIOS name are given: <c>host.domain</c> when both parts are there, else the DNS
    /// host name, else the computer name. Lower case; null when none is a DNS host name.
    /// </summary>
    public static string? FromNames(string? hostName, string? domainName, string? machineName)
    {
        var host = DnsHostname.Normalize(hostName);
        var domain = DnsHostname.Normalize(domainName);
        var candidates = new List<string>();

        if (host.Length > 0)
        {
            candidates.Add(domain.Length > 0 && !host.EndsWith("." + domain, StringComparison.Ordinal) ? $"{host}.{domain}" : host);
            candidates.Add(host);
        }

        candidates.Add(DnsHostname.Normalize(machineName));
        return candidates.FirstOrDefault(DnsHostname.IsValid);
    }

    /// <summary>
    /// The default for <paramref name="configuration"/>: <paramref name="computerName"/> when
    /// <paramref name="applies"/> and the configuration names neither a host name nor
    /// <c>Kestrel:Endpoints</c>; otherwise null.
    /// </summary>
    public static string? For(IConfiguration configuration, bool applies, Func<string?> computerName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(computerName);

        if (!applies || !string.IsNullOrWhiteSpace(configuration[Key]) || ManagedHttpsEndpoint.IsOverridden(configuration))
        {
            return null;
        }

        return computerName() is { } name && DnsHostname.IsValid(DnsHostname.Normalize(name)) ? DnsHostname.Normalize(name) : null;
    }

    /// <summary>
    /// Adds the computer's name as the host name of <paramref name="configuration"/> when
    /// <see cref="For"/> gives one, by the rule of <see cref="UsesComputerName"/>.
    /// <paramref name="serviceInstalled"/> is asked only when the answer depends on it.
    /// Returns the host name added, or null.
    /// </summary>
    public static string? Apply(
        ConfigurationManager configuration,
        DevConduitMode mode,
        bool runningAsService,
        Func<bool> serviceInstalled,
        Func<string?> computerName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceInstalled);

        if (!string.IsNullOrWhiteSpace(configuration[Key]))
        {
            return null;
        }

        var installed = mode == DevConduitMode.Server && !runningAsService && serviceInstalled();
        var hostname = For(configuration, UsesComputerName(mode, runningAsService, installed), computerName);
        if (hostname is not null)
        {
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(Key, hostname)]);
        }

        return hostname;
    }

    /// <summary>
    /// <paramref name="configuration"/> with <paramref name="hostname"/> as its host name when it
    /// is not null, for the tray, which reads the files the service reads and must see the host
    /// name the service would use.
    /// </summary>
    public static IConfiguration With(IConfiguration configuration, string? hostname) =>
        hostname is null
            ? configuration
            : new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection([new KeyValuePair<string, string?>(Key, hostname)])
                .Build();
}
