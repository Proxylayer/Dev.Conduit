using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Calls one server's <c>/cert</c> endpoints with this machine's client certificate. HTTP/1.1
/// only, because the server asks for the certificate after the request arrives, which HTTP/2
/// cannot do. The server's certificate is checked by <see cref="RemoteCertificatePolicy"/>:
/// Windows trust, or a certificate pinned for this host. No proxy is used.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CertApiClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;

    public CertApiClient(Uri baseAddress, X509Certificate2 identity, RemoteCertificatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout
        };
        handler.SslOptions.RemoteCertificateValidationCallback = policy.Validate;
        handler.SslOptions.ClientCertificates = [identity];
        handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => identity;

        _http = new HttpClient(handler) { Timeout = Timeout };
        _baseAddress = baseAddress;
    }

    public async Task<CertResponse> GetAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "cert");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<CertResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The server answered /cert with an empty body.");
    }

    public async Task<CertRenewResponse> RenewAsync(byte[] signingRequest, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "cert/renew");
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new CertRenewRequest(Convert.ToBase64String(signingRequest)), JsonSerializerOptions.Web));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<CertRenewResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The server answered /cert/renew with an empty body.");
    }

    public void Dispose() => _http.Dispose();

    private HttpRequestMessage Request(HttpMethod method, string path) =>
        new(method, new Uri(_baseAddress, path))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " (no client certificate reached the server)",
                HttpStatusCode.Forbidden => " (the server does not accept this client certificate: revoked, expired or from another server)",
                HttpStatusCode.Conflict => " (the server does not renew this client certificate yet: it has more than 60 days left by the server's clock)",
                HttpStatusCode.TooManyRequests => " (rate limited)",
                _ => string.Empty
            };
            throw new HttpRequestException($"The server answered {(int)response.StatusCode}{hint}.", null, response.StatusCode);
        }
    }
}
