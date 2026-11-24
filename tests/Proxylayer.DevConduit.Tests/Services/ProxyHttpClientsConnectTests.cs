using System.Net;
using System.Net.Sockets;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// The guarded connect step. Blocked literals are refused before any socket is dialled;
/// the one allowed connection goes to a loopback listener this test owns.
/// </summary>
public sealed class ProxyHttpClientsConnectTests
{
    private static EgressNetworkGuard DefaultGuard() => new(new DevConduitOptions().BlockedEgressNetworks);

    [Theory]
    [InlineData("169.254.169.254", "169.254.169.254")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("[::ffff:169.254.169.254]", "::ffff:169.254.169.254")]
    [InlineData("168.63.129.16", "168.63.129.16")]
    public async Task ConnectGuardedAsync_RefusesABlockedLiteralWithoutConnecting(string host, string expectedAddress)
    {
        var exception = await Assert.ThrowsAsync<EgressBlockedException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(
                DefaultGuard(),
                new DnsEndPoint(host, 80),
                TestContext.Current.CancellationToken));

        Assert.Equal(host, exception.Host);
        Assert.Equal(IPAddress.Parse(expectedAddress), exception.BlockedAddress);
    }

    [Fact]
    public async Task ConnectGuardedAsync_ConnectsToAnAllowedLiteral()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync(cancellationToken);

            var stream = await ProxyHttpClients.ConnectGuardedAsync(
                DefaultGuard(),
                new DnsEndPoint("127.0.0.1", port),
                cancellationToken);

            using var server = await accept;
            await using (stream)
            {
                Assert.True(stream.CanRead);
                Assert.True(stream.CanWrite);

                var payload = "ping"u8.ToArray();
                await server.GetStream().WriteAsync(payload, cancellationToken);

                var received = new byte[payload.Length];
                await stream.ReadExactlyAsync(received, cancellationToken);
                Assert.Equal(payload, received);
            }

            Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectGuardedAsync_RefusesNullArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(null!, new DnsEndPoint("127.0.0.1", 80), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ProxyHttpClients.ConnectGuardedAsync(DefaultGuard(), null!, CancellationToken.None));
    }
}
