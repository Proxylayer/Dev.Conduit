using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>Builds bound option instances without going through configuration binding.</summary>
internal static class TestOptions
{
    public static IOptions<DevConduitOptions> Create(Action<DevConduitOptions>? configure = null)
    {
        var options = new DevConduitOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }

    /// <summary>A routing configuration holding only these routes, each on the default remote.</summary>
    public static RoutingConfiguration Routing(params (string Name, string Target)[] routes) =>
        new([], [.. routes.Select(route => new RouteDefinition(route.Name, route.Target, null))], null, []);

    /// <summary>Parses a routing configuration from keys under Dev.Conduit, with an empty AzureAd section.</summary>
    public static RoutingConfiguration ParseRouting(params (string Key, string? Value)[] devConduit)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(devConduit.Select(entry =>
                new KeyValuePair<string, string?>($"{DevConduitOptions.SectionName}:{entry.Key}", entry.Value)))
            .Build();

        return RoutingConfigurationParser.Parse(
            configuration.GetSection(DevConduitOptions.SectionName),
            configuration.GetSection("AzureAd"));
    }
}
