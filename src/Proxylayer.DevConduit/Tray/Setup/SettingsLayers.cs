using System.Text;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>One configuration file in the layering, lowest precedence first.</summary>
/// <param name="Name">How the window names it: the file name for the appsettings files.</param>
/// <param name="Path">The full path.</param>
public sealed record SettingsLayer(string Name, string Path)
{
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// The configuration files a launch reads, in order: appsettings.json, the environment's
/// appsettings file, the machine settings file and the user settings file. Environment
/// variables and the command line sit above all four and are not shown.
/// </summary>
public sealed class SettingsLayers
{
    private readonly Dictionary<string, IConfiguration> _single = new(StringComparer.OrdinalIgnoreCase);

    public SettingsLayers(string contentRoot, string environmentName, SettingsFiles files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(files);

        ContentRoot = contentRoot;
        EnvironmentName = environmentName;
        Files = files;
        Layers =
        [
            new SettingsLayer("appsettings.json", Path.Combine(contentRoot, "appsettings.json")),
            new SettingsLayer($"appsettings.{environmentName}.json", Path.Combine(contentRoot, $"appsettings.{environmentName}.json")),
            new SettingsLayer("machine settings", files.MachinePath),
            new SettingsLayer("user settings", files.UserPath)
        ];
    }

    public string ContentRoot { get; }

    public string EnvironmentName { get; }

    public SettingsFiles Files { get; }

    public IReadOnlyList<SettingsLayer> Layers { get; }

    /// <summary>The environment name a console or tray launch of this process uses.</summary>
    public static string ProcessEnvironmentName() =>
        FirstNonBlank(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
        ?? Environments.Production;

    /// <summary>
    /// The environment name the installed service runs with: its registry environment, set by
    /// <c>install -environment:&lt;name&gt;</c>, else the machine's DOTNET_ENVIRONMENT, else Production.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string ServiceEnvironmentName() =>
        FirstNonBlank(
            Hosting.ServiceEnvironment.Get(Hosting.DevConduitServiceHost.ServiceName, Hosting.ServiceEnvironment.EnvironmentVariable),
            Environment.GetEnvironmentVariable(Hosting.ServiceEnvironment.EnvironmentVariable, EnvironmentVariableTarget.Machine))
        ?? Environments.Production;

    public SettingsLayer LayerFor(SettingsTarget target) => target == SettingsTarget.Machine ? Layers[2] : Layers[3];

    /// <summary>The layers below <paramref name="target"/>, lowest first.</summary>
    public IReadOnlyList<SettingsLayer> Below(SettingsTarget target) =>
        target == SettingsTarget.Machine ? [Layers[0], Layers[1]] : [Layers[0], Layers[1], Layers[2]];

    /// <summary>
    /// Reads the files for editing <paramref name="target"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">A file is not valid JSON or not a JSON object; the message names it.</exception>
    public SettingsView View(SettingsTarget target)
    {
        var below = Below(target);
        var document = new SettingsFileWriter(LayerFor(target).Path).Read();

        return new SettingsView(
            target,
            Build(below, null),
            document,
            key => below.LastOrDefault(layer => Single(layer).GetSection(key).Exists())?.Name,
            changed => Build(below, changed));
    }

    private IConfiguration Single(SettingsLayer layer)
    {
        if (!_single.TryGetValue(layer.Path, out var configuration))
        {
            configuration = Build([layer], null);
            _single[layer.Path] = configuration;
        }

        return configuration;
    }

    private static IConfiguration Build(IEnumerable<SettingsLayer> layers, JsonObject? top)
    {
        var builder = new ConfigurationBuilder();
        foreach (var layer in layers)
        {
            if (!SettingsFiles.TryCheck(layer.Path, out var hasContent, out var error))
            {
                throw new InvalidDataException(error);
            }

            if (hasContent)
            {
                builder.AddJsonFile(layer.Path, optional: true, reloadOnChange: false);
            }
        }

        if (top is not null)
        {
            builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(top.ToJsonString())));
        }

        try
        {
            return builder.Build();
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            throw new InvalidDataException(exception.InnerException?.Message ?? exception.Message, exception);
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
