using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray;

/// <summary>How an elevated action ended.</summary>
/// <param name="Succeeded">True when the helper reported success.</param>
/// <param name="Cancelled">True when the UAC prompt was declined; nothing was done and nothing need be said.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record ServiceAdminOutcome(bool Succeeded, bool Cancelled, string Message);

/// <summary>
/// Starts the elevated helper, <c>Dev.Conduit.exe service-admin ...</c>, with the
/// <c>runas</c> verb, waits for it, and turns its exit code into a message. The helper's
/// console window is hidden.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceAdminClient
{
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private readonly string _executablePath;

    public ServiceAdminClient(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    public static ServiceAdminClient ForThisProcess() =>
        new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Dev.Conduit.exe"));

    public async Task<ServiceAdminOutcome> RunAsync(string action, params string[] arguments)
    {
        var started = Start(action, arguments);
        if (started.Process is not { } process)
        {
            return started.Outcome;
        }

        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = process.ExitCode;
            return new ServiceAdminOutcome(exitCode == (int)ServiceAdminExitCode.Success, false, ServiceAdminExitCodes.Describe(exitCode));
        }
    }

    /// <summary>
    /// Starts the helper without waiting for it: the UAC prompt is answered before this returns.
    /// The caller owns the returned process. A declined prompt comes back as
    /// <see cref="ServiceAdminOutcome.Cancelled"/> with no process.
    /// </summary>
    public ServiceAdminStart Start(string action, params string[] arguments)
    {
        var info = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = CommandLine.Join([ServiceLaunch.ServiceAdminVerb, action, .. arguments])
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new ServiceAdminStart(null, new ServiceAdminOutcome(Succeeded: false, Cancelled: true, string.Empty));
        }
        catch (Win32Exception exception)
        {
            return new ServiceAdminStart(null, new ServiceAdminOutcome(false, false, $"The administrative helper could not be started: {exception.Message}"));
        }

        return process is null
            ? new ServiceAdminStart(null, new ServiceAdminOutcome(false, false, "The administrative helper could not be started."))
            : new ServiceAdminStart(process, new ServiceAdminOutcome(true, false, "The administrative helper started."));
    }
}

/// <summary>A helper started without waiting: its process, or how starting it ended.</summary>
/// <param name="Process">The running helper; null when it did not start.</param>
/// <param name="Outcome">Started, or why not (<see cref="ServiceAdminOutcome.Cancelled"/> for a declined UAC prompt).</param>
public sealed record ServiceAdminStart(Process? Process, ServiceAdminOutcome Outcome);
