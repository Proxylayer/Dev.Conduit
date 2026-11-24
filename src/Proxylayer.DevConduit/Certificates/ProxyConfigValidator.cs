using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>A <c>.conduit</c> that passed every check, with its certificates parsed.</summary>
/// <param name="AddressIsIp">True when <see cref="Address"/> is an IP address, so it can go into the hosts file.</param>
/// <param name="ServerCertificates">The server certificates that are still valid, without keys.</param>
/// <param name="ClientCertificatePkcs12">The client certificate and key exactly as the file carries them.</param>
/// <param name="ClientCertificate">The client certificate, for reporting; its key is ephemeral.</param>
/// <param name="Scope">The delegated scope the server accepts, a plain name; null when the file carries none.</param>
public sealed record ValidatedProxyConfig(
    string Hostname,
    string Address,
    bool AddressIsIp,
    int Port,
    string Remote,
    string TenantId,
    string ClientId,
    IReadOnlyList<KeyValuePair<string, string>> Routes,
    IReadOnlyList<X509Certificate2> ServerCertificates,
    byte[] ClientCertificatePkcs12,
    X509Certificate2 ClientCertificate,
    string? Scope = null)
{
    /// <summary>The remote's endpoint as the client configures it.</summary>
    public string Endpoint => $"https://{Hostname}:{Port}";
}

/// <summary>
/// Checks a <c>.conduit</c> strictly before anything on the machine changes: the format,
/// a host name every server certificate names, server certificates that are self-signed,
/// CA=false and for server authentication, a client certificate with its key and the client
/// authentication usage, Entra ids, routes that are a name and an http or https origin, and,
/// when present, a scope that is a plain scope name. Pure apart from the clock passed in.
/// </summary>
public static partial class ProxyConfigValidator
{
    private const int MaxServerCertificates = 4;

    /// <summary>The longest scope name a file may carry.</summary>
    public const int MaxScopeLength = 120;

    public static bool TryValidate(
        ReadOnlySpan<byte> utf8,
        DateTimeOffset now,
        [NotNullWhen(true)] out ValidatedProxyConfig? config,
        out string error)
    {
        config = null;

        ProxyConfigDocument document;
        try
        {
            document = ProxyConfigDocument.Parse(utf8);
        }
        catch (JsonException exception)
        {
            error = $"The file is not a valid .conduit: {exception.Message}";
            return false;
        }

        return TryValidate(document, now, out config, out error);
    }

    public static bool TryValidate(
        ProxyConfigDocument document,
        DateTimeOffset now,
        [NotNullWhen(true)] out ValidatedProxyConfig? config,
        out string error)
    {
        config = null;

        if (!ProxyConfigDocument.IsAcceptedFormat(document.Format))
        {
            error = $"The file's format is '{document.Format}', not {ProxyConfigDocument.CurrentFormat}. Export it again from a server of this version.";
            return false;
        }

        var hostname = DnsHostname.Normalize(document.Hostname);
        if (!DnsHostname.IsValid(hostname))
        {
            error = $"The host name '{document.Hostname}' is not a DNS host name.";
            return false;
        }

        var address = document.Address?.Trim() ?? string.Empty;
        if (!DnsHostname.IsAddressOrHostname(address))
        {
            error = $"The address '{document.Address}' is not an IP address or a host name.";
            return false;
        }

        if (document.Port is < 1 or > 65535)
        {
            error = $"The port {document.Port} is not a TCP port.";
            return false;
        }

        if (!IsName(document.Remote))
        {
            error = $"The remote name '{document.Remote}' must be 1 to 64 letters, digits, dots, dashes or underscores.";
            return false;
        }

        if (!IsTenant(document.TenantId) || !Guid.TryParse(document.ClientId, out _))
        {
            error = "The file does not carry a usable Entra tenant id and client id.";
            return false;
        }

        if (document.Scope is { } scope && !IsScopeName(scope))
        {
            error = $"The scope '{scope}' must be 1 to {MaxScopeLength} letters, digits, dots, dashes or underscores, such as DevConduit.Forward.";
            return false;
        }

        if (!TryReadRoutes(document.Routes, out var routes, out error)
            || !TryReadServerCertificates(document.ServerCertificates, hostname, now, out var serverCertificates, out error)
            || !TryReadClientCertificate(document.ClientCertificate, now, out var clientPkcs12, out var clientCertificate, out error))
        {
            return false;
        }

        config = new ValidatedProxyConfig(
            hostname,
            address,
            IPAddress.TryParse(address, out _),
            document.Port,
            document.Remote.Trim(),
            document.TenantId.Trim(),
            document.ClientId.Trim(),
            routes,
            serverCertificates,
            clientPkcs12,
            clientCertificate,
            document.Scope?.Trim());
        error = string.Empty;
        return true;
    }

    /// <summary>A remote or route name: also a configuration key segment, so no colon.</summary>
    public static bool IsName(string? value) => !string.IsNullOrWhiteSpace(value) && Name().IsMatch(value.Trim());

    /// <summary>
    /// A delegated scope's own name, as an app registration exposes it and a token's <c>scp</c>
    /// claim carries it: never a full <c>api://</c> URI.
    /// </summary>
    public static bool IsScopeName(string? value) => !string.IsNullOrWhiteSpace(value) && ScopeName().IsMatch(value.Trim());

    private static bool IsTenant(string? value) =>
        Guid.TryParse(value, out _) || DnsHostname.IsValid(DnsHostname.Normalize(value));

    private static bool TryReadRoutes(
        IReadOnlyDictionary<string, string>? source,
        out IReadOnlyList<KeyValuePair<string, string>> routes,
        out string error)
    {
        var list = new List<KeyValuePair<string, string>>();
        routes = list;
        error = string.Empty;

        foreach (var (name, target) in source ?? new Dictionary<string, string>())
        {
            if (!IsName(name))
            {
                error = $"The route name '{name}' must be 1 to 64 letters, digits, dots, dashes or underscores.";
                return false;
            }

            if (!RouteOrigin.TryParse(target, out var origin, out var problem))
            {
                error = $"The route '{name}' has an unusable target: {problem}";
                return false;
            }

            if (list.Any(existing => string.Equals(existing.Key, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = $"The route '{name}' appears twice.";
                return false;
            }

            list.Add(new KeyValuePair<string, string>(name.Trim(), origin.GetLeftPart(UriPartial.Authority)));
        }

        return true;
    }

    private static bool TryReadServerCertificates(
        IReadOnlyList<string>? source,
        string hostname,
        DateTimeOffset now,
        out IReadOnlyList<X509Certificate2> certificates,
        out string error)
    {
        var list = new List<X509Certificate2>();
        certificates = list;

        if (source is null || source.Count is 0 or > MaxServerCertificates)
        {
            error = $"The file must carry 1 to {MaxServerCertificates} server certificates.";
            return false;
        }

        foreach (var encoded in source)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded ?? string.Empty));
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                error = "A server certificate in the file is not a DER certificate.";
                return false;
            }

            if (ServerCertificateRules.Problem(certificate, hostname) is { } problem)
            {
                error = problem;
                return false;
            }

            if (now < certificate.NotAfter.ToUniversalTime() && list.All(existing => existing.Thumbprint != certificate.Thumbprint))
            {
                list.Add(certificate);
            }
        }

        if (list.Count == 0)
        {
            error = "Every server certificate in the file has expired. Export a new file from the server.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadClientCertificate(
        string? encoded,
        DateTimeOffset now,
        out byte[] pkcs12,
        [NotNullWhen(true)] out X509Certificate2? certificate,
        out string error)
    {
        certificate = null;
        try
        {
            pkcs12 = Convert.FromBase64String(encoded ?? string.Empty);
            certificate = CertificateFactory.LoadPkcs12(pkcs12, ephemeral: true);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            pkcs12 = [];
            error = "The client certificate in the file is not a PKCS#12 certificate without a password.";
            return false;
        }

        if (!certificate.HasPrivateKey
            || !CertificateFactory.HasUsage(certificate, CertificateFactory.ClientAuthOid)
            || CertificateFactory.IsAuthority(certificate))
        {
            error = "The client certificate in the file has no private key, is not for client authentication, or is a certificate authority.";
            return false;
        }

        if (now >= certificate.NotAfter.ToUniversalTime())
        {
            error = $"The client certificate in the file expired on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}. Export a new file from the server.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,120}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeName();
}
