using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>Which client credential an <c>AzureAd</c> section describes, and that the description never leaks it.</summary>
public sealed class EntraCredentialTests
{
    private const string SecretValue = "s3cr3t-value-that-must-not-leak";

    private static IConfigurationSection AzureAd(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>($"AzureAd:{value.Key}", value.Value)))
            .Build()
            .GetSection("AzureAd");

    [Fact]
    public void Describe_ReportsNoneForAnEmptySection()
    {
        var credential = EntraCredential.Describe(AzureAd());

        Assert.Equal(EntraCredentialKind.None, credential.Kind);
        Assert.Null(credential.ManagedIdentityClientId);
        Assert.Null(credential.SourceType);
        Assert.False(credential.IsSystemAssignedManagedIdentity);
    }

    [Fact]
    public void Describe_ReportsNoneForAnEmptyTopLevelSecret()
    {
        var credential = EntraCredential.Describe(AzureAd(("ClientSecret", "")));

        Assert.Equal(EntraCredentialKind.None, credential.Kind);
    }

    [Fact]
    public void Describe_FallsBackToTheTopLevelSecret()
    {
        var credential = EntraCredential.Describe(AzureAd(("ClientSecret", SecretValue)));

        Assert.Equal(EntraCredentialKind.ClientSecret, credential.Kind);
        Assert.Contains("AzureAd:ClientSecret", credential.Detail);
        Assert.Null(credential.SourceType);
    }

    [Fact]
    public void Describe_SurfacesTheUserAssignedManagedIdentityClientId()
    {
        const string clientId = "11111111-2222-3333-4444-555555555555";

        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "SignedAssertionFromManagedIdentity"),
            ("ClientCredentials:0:ManagedIdentityClientId", clientId)));

        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
        Assert.Equal(clientId, credential.ManagedIdentityClientId);
        Assert.Equal(EntraCredential.ManagedIdentitySourceType, credential.SourceType);
        Assert.False(credential.IsSystemAssignedManagedIdentity);
        Assert.Contains(clientId, credential.Detail);
        Assert.Contains("AzureAd:ClientCredentials:0", credential.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_TreatsAManagedIdentityWithoutAClientIdAsSystemAssigned(string? clientId)
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "SignedAssertionFromManagedIdentity"),
            ("ClientCredentials:0:ManagedIdentityClientId", clientId)));

        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
        Assert.Null(credential.ManagedIdentityClientId);
        Assert.True(credential.IsSystemAssignedManagedIdentity);
        Assert.Contains("system-assigned", credential.Detail);
    }

    [Theory]
    [InlineData("signedassertionfrommanagedidentity", EntraCredentialKind.ManagedIdentity)]
    [InlineData("SIGNEDASSERTIONFROMMANAGEDIDENTITY", EntraCredentialKind.ManagedIdentity)]
    [InlineData("clientsecret", EntraCredentialKind.ClientSecret)]
    [InlineData("CLIENTSECRET", EntraCredentialKind.ClientSecret)]
    public void Describe_MatchesTheSourceTypeIgnoringCase(string sourceType, EntraCredentialKind expected)
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", sourceType),
            ("ClientCredentials:0:ClientSecret", SecretValue)));

        Assert.Equal(expected, credential.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Describe_SkipsAClientSecretEntryWithoutASecretInFavourOfALaterEntry(string? secret)
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "ClientSecret"),
            ("ClientCredentials:0:ClientSecret", secret),
            ("ClientCredentials:1:SourceType", "SignedAssertionFromManagedIdentity")));

        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
        Assert.Contains("AzureAd:ClientCredentials:1", credential.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_SkipsAnEntryWithoutASourceType(string? sourceType)
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", sourceType),
            ("ClientCredentials:0:ClientSecret", "ignored"),
            ("ClientCredentials:1:SourceType", "SignedAssertionFromManagedIdentity")));

        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
        Assert.Contains("AzureAd:ClientCredentials:1", credential.Detail);
    }

    [Fact]
    public void Describe_UsesTheFirstUsableEntry()
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "ClientSecret"),
            ("ClientCredentials:0:ClientSecret", SecretValue),
            ("ClientCredentials:1:SourceType", "SignedAssertionFromManagedIdentity"),
            ("ClientCredentials:1:ManagedIdentityClientId", "later-identity")));

        Assert.Equal(EntraCredentialKind.ClientSecret, credential.Kind);
        Assert.Equal(EntraCredential.ClientSecretSourceType, credential.SourceType);
        Assert.Null(credential.ManagedIdentityClientId);
        Assert.Contains("AzureAd:ClientCredentials:0", credential.Detail);
    }

    [Fact]
    public void Describe_PrefersAUsableEntryOverTheTopLevelSecret()
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientSecret", SecretValue),
            ("ClientCredentials:0:SourceType", "SignedAssertionFromManagedIdentity")));

        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
    }

    [Fact]
    public void Describe_FallsBackToTheTopLevelSecretWhenNoEntryIsUsable()
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientSecret", SecretValue),
            ("ClientCredentials:0:SourceType", "ClientSecret"),
            ("ClientCredentials:0:ClientSecret", "")));

        Assert.Equal(EntraCredentialKind.ClientSecret, credential.Kind);
        Assert.Contains("AzureAd:ClientSecret", credential.Detail);
    }

    [Fact]
    public void Describe_ReportsAnUnknownSourceTypeAsOther()
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "KeyVault"),
            ("ClientCredentials:1:SourceType", "SignedAssertionFromManagedIdentity")));

        Assert.Equal(EntraCredentialKind.Other, credential.Kind);
        Assert.Equal("KeyVault", credential.SourceType);
        Assert.Contains("KeyVault", credential.Detail);
    }

    [Fact]
    public void Describe_NeverPutsATopLevelSecretInTheDetail()
    {
        var credential = EntraCredential.Describe(AzureAd(("ClientSecret", SecretValue)));

        Assert.DoesNotContain(SecretValue, credential.Detail);
    }

    [Fact]
    public void Describe_NeverPutsAClientCredentialsSecretInTheDetail()
    {
        var credential = EntraCredential.Describe(AzureAd(
            ("ClientCredentials:0:SourceType", "ClientSecret"),
            ("ClientCredentials:0:ClientSecret", SecretValue)));

        Assert.Equal(EntraCredentialKind.ClientSecret, credential.Kind);
        Assert.DoesNotContain(SecretValue, credential.Detail);
    }

    [Fact]
    public void Describe_RefusesANullSection() =>
        Assert.Throws<ArgumentNullException>(() => EntraCredential.Describe(null!));
}
