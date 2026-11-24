using System.Runtime.Versioning;
using System.Security.Principal;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// <c>Dev.Conduit.exe import &lt;file&gt; [--machine]</c>: applies a server's
/// <c>.conduit</c> to this workstation. The file is checked first, unelevated. The import
/// changes the hosts file, <c>LocalMachine\Root</c>, <c>%ProgramData%</c> and the machine
/// settings file, so it needs administrator rights: from an elevated prompt it runs here, and
/// <c>--machine</c> insists on that; from an ordinary prompt it hands a copy of the file to the
/// elevated helper, <c>service-admin import</c>, which shows a UAC prompt. The MSI runs
/// <c>import &lt;file&gt; --machine</c> as LocalSystem during a Workstation install given
/// <c>CONDUITCONFIG</c>; nothing here depends on an interactive user.
/// <c>import &lt;file&gt; --seed-server</c> is for a server instead: it copies only the file's Entra
/// ids and routes into this machine's settings (<see cref="ServerSeedSettings"/>), elevated the
/// same way, through <c>service-admin seed</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImportCommand
{
    public const string Verb = "import";
    public const string MachineOption = "--machine";
    public const string SeedServerOption = "--seed-server";

    public static int Run(string[] args)
    {
        var machine = args.Any(argument => string.Equals(argument, MachineOption, StringComparison.OrdinalIgnoreCase));
        var seed = args.Any(argument => string.Equals(argument, SeedServerOption, StringComparison.OrdinalIgnoreCase));
        var files = args
            .Where(argument => !string.Equals(argument, MachineOption, StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(argument, SeedServerOption, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count != 1)
        {
            Console.Error.WriteLine($"Usage: Dev.Conduit.exe {Verb} <file{ProxyConfigDocument.FileExtension}> [{MachineOption}]");
            Console.Error.WriteLine($"       Dev.Conduit.exe {Verb} <file{ProxyConfigDocument.FileExtension}> {SeedServerOption} [{MachineOption}]");
            return (int)ServiceAdminExitCode.InvalidArguments;
        }

        byte[] content;
        try
        {
            content = ProxyConfigTransfer.ReadLimited(Path.GetFullPath(files[0]));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"The file could not be read: {exception.Message}");
            return (int)ServiceAdminExitCode.ImportRejected;
        }

        if (!ProxyConfigValidator.TryValidate(content, TimeProvider.System.GetUtcNow(), out var config, out var error))
        {
            Console.Error.WriteLine(error);
            return (int)ServiceAdminExitCode.ImportRejected;
        }

        Console.WriteLine($"{files[0]}: server {config.Hostname}:{config.Port}, address {config.Address}, remote '{config.Remote}', {config.Routes.Count} route(s).");

        if (IsElevated())
        {
            return seed
                ? (int)Seed(config, SettingsFiles.ForThisComputer().MachinePath, protect: true, Console.Out)
                : (int)Apply(config, ProxyConfigImportTargets.ForThisComputer(), Console.Out);
        }

        if (machine)
        {
            Console.Error.WriteLine($"{MachineOption} imports in this process, which needs an elevated prompt. Run it again as administrator, or leave out {MachineOption} to be asked for elevation.");
            return (int)ServiceAdminExitCode.NotElevated;
        }

        return seed
            ? HandToHelper(files[0], ServiceAdminCommand.SeedAction, "seed this server's settings", ServerSeedSettings.ManagedIdentityNote)
            : HandToHelper(files[0], ServiceAdminCommand.ImportAction, "import", "Restart the local proxy so it reads the new remote.");
    }

    /// <summary>
    /// Seeds a server's machine settings file at <paramref name="machineSettingsPath"/> from a
    /// checked file: the Entra ids and the routes only, nothing outside <see cref="ManagedSettingKeys.Seed"/>.
    /// Shared with the elevated helper.
    /// </summary>
    /// <param name="protect">Write the file with the machine settings access list, as on the real machine.</param>
    public static ServiceAdminExitCode Seed(ValidatedProxyConfig config, string machineSettingsPath, bool protect, TextWriter output)
    {
        var seed = ServerSeedSettings.From(config);
        var changes = seed.Changes;
        if (ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Seed).Count > 0)
        {
            return ServiceAdminExitCode.SettingsRejected;
        }

        try
        {
            var writer = new SettingsFileWriter(machineSettingsPath, protect ? MachineSettingsAccess.BuildSecurity : null);
            var lines = seed.Describe(writer.Read());
            writer.Apply(changes);

            output.WriteLine($"  Machine settings {machineSettingsPath}:");
            foreach (var line in lines)
            {
                output.WriteLine("    " + line);
            }

            output.WriteLine("  The host name, port and client address are this server's own and were not changed.");
            output.WriteLine("  " + ServerSeedSettings.ManagedIdentityNote);
            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            output.WriteLine($"The machine settings could not be written: {exception.Message}");
            return ServiceAdminExitCode.SettingsWriteFailed;
        }
    }

    /// <summary>Applies a checked file; shared with the elevated helper.</summary>
    public static ServiceAdminExitCode Apply(ValidatedProxyConfig config, ProxyConfigImportTargets targets, TextWriter output)
    {
        try
        {
            foreach (var line in new ProxyConfigImporter(targets, TimeProvider.System).Import(config))
            {
                output.WriteLine("  " + line);
            }

            return ServiceAdminExitCode.Success;
        }
        catch (InvalidOperationException exception)
        {
            output.WriteLine($"The import was refused: {exception.Message}");
            return ServiceAdminExitCode.ImportRejected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or System.Security.Cryptography.CryptographicException or TimeoutException)
        {
            output.WriteLine($"The import failed: {exception.Message}");
            return ServiceAdminExitCode.ImportFailed;
        }
    }

    private static int HandToHelper(string file, string action, string what, string done)
    {
        string transfer;
        try
        {
            transfer = ProxyConfigTransfer.WriteTempFile(Path.GetFullPath(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"The file could not be copied for the elevated helper: {exception.Message}");
            return (int)ServiceAdminExitCode.ImportFailed;
        }

        try
        {
            Console.WriteLine($"Asking for administrator rights to {what}...");
            var outcome = ServiceAdminClient.ForThisProcess().RunAsync(action, transfer).GetAwaiter().GetResult();
            if (outcome.Cancelled)
            {
                Console.Error.WriteLine("The elevation prompt was declined; nothing was changed.");
                return (int)ServiceAdminExitCode.NotElevated;
            }

            Console.WriteLine(outcome.Succeeded ? $"Done. {done}" : outcome.Message);
            return outcome.Succeeded ? 0 : (int)ServiceAdminExitCode.ImportFailed;
        }
        finally
        {
            ProxyConfigTransfer.Delete(transfer);
        }
    }

    /// <summary>
    /// An administrator's elevated token, or LocalSystem: the MSI runs a Workstation install's
    /// import as SYSTEM, from a deferred custom action.
    /// </summary>
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
