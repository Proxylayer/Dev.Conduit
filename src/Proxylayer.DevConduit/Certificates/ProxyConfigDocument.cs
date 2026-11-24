using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// A <c>&lt;hostname&gt;.conduit</c> file: everything a client needs to use one Dev.Conduit
/// server, as UTF-8 JSON. It carries a client certificate with its private key, so the file is
/// a secret: whoever holds it can call <c>/cert</c> as that client, which fetches public
/// certificates and renews that client certificate, and nothing more.
/// </summary>
/// <param name="Format"><see cref="CurrentFormat"/> when written; <see cref="LegacyFormat"/> is also read.</param>
/// <param name="Hostname">The server's host name, the only name its certificate carries.</param>
/// <param name="Address">What clients map the host name to: an IP address for the hosts file, or a name DNS already resolves.</param>
/// <param name="Port">The server's HTTPS port.</param>
/// <param name="Remote">The name the client gives this server under <c>DevConduit:Remotes</c>.</param>
/// <param name="TenantId">The Entra tenant clients sign in to for this server.</param>
/// <param name="ClientId">The app registration clients request tokens for.</param>
/// <param name="Routes">The server's routes, name to upstream origin.</param>
/// <param name="ServerCertificates">The server certificates to trust, current and next, DER, base64.</param>
/// <param name="ClientCertificate">The client certificate and its key, PKCS#12 with no password, base64.</param>
/// <param name="ExportedAt">When the server wrote the file.</param>
/// <param name="Scope">
/// The delegated scope the server accepts, a plain name such as <c>DevConduit.Forward</c>, which
/// clients request as <c>api://{ClientId}/{Scope}</c>. Optional: files written before it was
/// carried have none, and clients then use the default. Not written when null.
/// </param>
public sealed record ProxyConfigDocument(
    string Format,
    string Hostname,
    string Address,
    int Port,
    string Remote,
    string TenantId,
    string ClientId,
    IReadOnlyDictionary<string, string> Routes,
    IReadOnlyList<string> ServerCertificates,
    string ClientCertificate,
    DateTimeOffset ExportedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null)
{
    /// <summary>The format every export writes.</summary>
    public const string CurrentFormat = "dev-conduit/1";

    /// <summary>
    /// The format of files exported by the product's previous name, POC.DevProxy: the same
    /// schema under another name, still accepted on import.
    /// </summary>
    public const string LegacyFormat = "poc-devproxy/1";

    /// <summary>The extension every export writes.</summary>
    public const string FileExtension = ".conduit";

    /// <summary>The extension of files exported by POC.DevProxy, still accepted on import.</summary>
    public const string LegacyFileExtension = ".proxyconfig";

    /// <summary>A file this large is not a client configuration file.</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The file name an export of <paramref name="hostname"/> is written to.</summary>
    public static string FileNameFor(string hostname) => DnsHostname.Normalize(hostname) + FileExtension;

    /// <summary>True for a format an import accepts: <see cref="CurrentFormat"/> or <see cref="LegacyFormat"/>, exactly.</summary>
    public static bool IsAcceptedFormat(string? format) =>
        string.Equals(format, CurrentFormat, StringComparison.Ordinal)
        || string.Equals(format, LegacyFormat, StringComparison.Ordinal);

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>Parses the file's text, refusing unknown properties. Throws <see cref="JsonException"/> on anything else.</summary>
    public static ProxyConfigDocument Parse(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<ProxyConfigDocument>(utf8, Options)
        ?? throw new JsonException("The file is empty.");
}
