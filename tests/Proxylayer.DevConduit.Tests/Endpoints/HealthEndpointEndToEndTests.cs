using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Endpoints;

/// <summary>
/// <c>/health</c> on a fresh, blank server: built exactly as a launch builds it, with no Entra ids
/// at all, on a free loopback port, with throwaway settings files and data root and Windows trust
/// left alone. It answers 200 before sign-in runs, while every other path gets the 503 notice.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HealthEndpointEndToEndTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string[] BlankServer(SettingsLayout layout, params string[] extra) =>
    [
        "--mode", "server",
        "--AzureAd:TenantId=",
        "--AzureAd:ClientId=",
        $"--DevConduit:DataRoot={Path.Combine(layout.Root, "data")}",
        "--DevConduit:Server:TrustStore=None",
        .. extra
    ];

    [Fact]
    public async Task Health_AnswersOkOnAServerWithBlankAzureAd()
    {
        using var layout = new SettingsLayout();
        var port = FreePort();

        Assert.True(
            DevConduitApplication.TryBuild(
                BlankServer(layout, $"--Kestrel:Endpoints:Http:Url=http://127.0.0.1:{port}"),
                runningAsService: false,
                layout.Files,
                NullLoggerProvider.Instance,
                out var app,
                out var fatal),
            fatal);

        await using (app)
        {
            await AssertHealthyAsync(app, new Uri($"http://127.0.0.1:{port}/"), new SocketsHttpHandler { UseProxy = false });
        }
    }

    [Fact]
    public async Task Health_AnswersOkBehindTheManagedHttpsEndpoint()
    {
        using var layout = new SettingsLayout();
        var port = FreePort();

        Assert.True(
            DevConduitApplication.TryBuild(
                BlankServer(layout, "--DevConduit:Server:Hostname=localhost", $"--DevConduit:Server:Port={port}"),
                runningAsService: false,
                layout.Files,
                NullLoggerProvider.Instance,
                out var app,
                out var fatal),
            fatal);

        // The managed endpoint listens on every interface; a test keeps to loopback.
        app.Configuration["Kestrel:Endpoints:ManagedHttps:Url"] = $"https://localhost:{port}";

        await using (app)
        {
            var handler = new SocketsHttpHandler { UseProxy = false };
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            await AssertHealthyAsync(app, new Uri($"https://localhost:{port}/"), handler);
        }
    }

    private static async Task AssertHealthyAsync(WebApplication app, Uri baseAddress, HttpMessageHandler handler)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await app.StartAsync(cancel.Token);
        try
        {
            using var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };

            using (var response = await client.GetAsync("health", cancel.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel.Token))!.AsObject();
                Assert.Equal("ok", (string?)body["status"]);
                Assert.False((bool)body["configured"]!);
                Assert.Equal(["status", "configured"], body.Select(property => property.Key));
            }

            // Everything else still gets the notice saying what to configure.
            using (var response = await client.GetAsync("api/me", cancel.Token))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("NOT configured for Microsoft Entra ID", await response.Content.ReadAsStringAsync(cancel.Token), StringComparison.Ordinal);
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
