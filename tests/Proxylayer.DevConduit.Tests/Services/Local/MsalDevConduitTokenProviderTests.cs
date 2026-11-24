using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services.Local;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services.Local;

/// <summary>
/// The parts of the MSAL token provider that run before any MSAL client exists. Anything past
/// that point either contacts Entra or opens a sign-in, so it is not exercised here.
/// </summary>
public sealed class MsalDevConduitTokenProviderTests
{
    private static RemoteDefinition Remote(string name, string tenantId, string clientId) =>
        new(
            name,
            "https://devconduit.corp:8091",
            new Uri("https://devconduit.corp:8091/"),
            new RemoteIdentity(tenantId, clientId),
            InheritsTenantId: false,
            InheritsClientId: false,
            [$"api://{clientId}/DevConduit.Forward"],
            []);

    [Theory]
    [InlineData("", "")]
    [InlineData("11111111-1111-1111-1111-111111111111", "")]
    [InlineData("", "22222222-2222-2222-2222-222222222222")]
    [InlineData("<YOUR-TENANT-ID>", "<YOUR-CLIENT-ID>")]
    public async Task GetAccessTokenAsync_NamesTheRemoteWhenItsIdentityIsIncomplete(string tenantId, string clientId)
    {
        using var cache = new TempDirectory();
        using var provider = new MsalDevConduitTokenProvider(
            TestOptions.Create(options =>
            {
                options.Local.CacheDirectory = cache.Path;
                options.Local.InteractiveFallback = false;
            }),
            NullLogger<MsalDevConduitTokenProvider>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.GetAccessTokenAsync(Remote("prod", tenantId, clientId), TestContext.Current.CancellationToken));

        Assert.Contains("remote 'prod'", exception.Message);
        Assert.Contains("DevConduit:Remotes:prod:TenantId", exception.Message);

        // The check runs before the token cache is opened.
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }
}
