namespace Proxylayer.DevConduit.Configuration;


/// <summary>
/// Works out which mode this process runs in: the <c>--mode</c> argument first, then
/// <c>DevConduit:Mode</c>, then <see cref="DevConduitMode.Server"/>.
/// </summary>
public static class DevConduitModeSelection
{
    public const string Argument = "--mode";

    /// <summary>The configuration key the resolved mode is written back to.</summary>
    public const string ConfigurationKey = $"{DevConduitOptions.SectionName}:Mode";

    /// <summary>
    /// Resolves the mode and writes it back to configuration so bound options agree with it.
    /// Returns false with a message when a value was supplied but is not a mode name.
    /// </summary>
    public static bool TryResolve(ConfigurationManager configuration, string[] args, out DevConduitMode mode, out string error)
    {
        mode = DevConduitMode.Server;
        error = string.Empty;

        var fromArguments = FromArguments(args);
        var value = fromArguments ?? configuration[ConfigurationKey];
        var source = fromArguments is null ? ConfigurationKey : Argument;

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!Enum.TryParse(value.Trim(), ignoreCase: true, out mode) || !Enum.IsDefined(mode))
            {
                error = $"'{value}' is not a Dev.Conduit mode. {source} accepts 'server' or 'local'.";
                mode = DevConduitMode.Server;
                return false;
            }
        }

        // Added as the last source so it wins over appsettings and the environment, and so
        // the options bound into the container agree with the mode this process chose.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [ConfigurationKey] = mode.ToString() });
        return true;
    }

    /// <summary>Reads <c>--mode local</c> or <c>--mode=local</c> from the command line.</summary>
    private static string? FromArguments(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))
            {
                return argument[(Argument.Length + 1)..];
            }

            if (string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length ? args[index + 1] : string.Empty;
            }
        }

        return null;
    }
}
