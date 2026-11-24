using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// How the service's environment block is rewritten to carry DOTNET_ENVIRONMENT. Only the pure
/// merge is exercised; the registry is never written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceEnvironmentTests
{
    private const string Name = ServiceEnvironment.EnvironmentVariable;

    [Fact]
    public void EnvironmentVariable_IsDotnetEnvironment() =>
        Assert.Equal("DOTNET_ENVIRONMENT", ServiceEnvironment.EnvironmentVariable);

    [Fact]
    public void Merge_AddsTheVariableToAnEmptyBlock() =>
        Assert.Equal(["DOTNET_ENVIRONMENT=Uat"], ServiceEnvironment.Merge([], Name, "Uat"));

    [Fact]
    public void Merge_ReplacesAnExistingAssignment() =>
        Assert.Equal(
            ["DOTNET_ENVIRONMENT=Production"],
            ServiceEnvironment.Merge(["DOTNET_ENVIRONMENT=Uat"], Name, "Production"));

    [Theory]
    [InlineData("dotnet_environment=Uat")]
    [InlineData("Dotnet_Environment=Uat")]
    [InlineData("DOTNET_ENVIRONMENT=")]
    public void Merge_ReplacesAnAssignmentWhateverItsCase(string existing) =>
        Assert.Equal(["DOTNET_ENVIRONMENT=Production"], ServiceEnvironment.Merge([existing], Name, "Production"));

    [Fact]
    public void Merge_ReplacesEveryAssignmentWhenThereAreSeveral() =>
        Assert.Equal(
            ["OTHER=1", "DOTNET_ENVIRONMENT=Uat"],
            ServiceEnvironment.Merge(["DOTNET_ENVIRONMENT=Development", "OTHER=1", "dotnet_environment=Test"], Name, "Uat"));

    [Fact]
    public void Merge_KeepsOtherEntriesInOrderAndAppendsTheVariable() =>
        Assert.Equal(
            ["ASPNETCORE_URLS=http://+:8091", "DOTNET_ENVIRONMENT_EXTRA=1", "PATH=C:\\tools", "DOTNET_ENVIRONMENT=Uat"],
            ServiceEnvironment.Merge(
                ["ASPNETCORE_URLS=http://+:8091", "DOTNET_ENVIRONMENT_EXTRA=1", "DOTNET_ENVIRONMENT=Old", "PATH=C:\\tools"],
                Name,
                "Uat"));

    [Fact]
    public void Merge_DropsNullAndEmptyEntries() =>
        Assert.Equal(
            ["OTHER=1", "DOTNET_ENVIRONMENT=Uat"],
            ServiceEnvironment.Merge([null!, "", "OTHER=1", ""], Name, "Uat"));
}
