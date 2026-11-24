using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray.Updates;

public enum HandoffResult
{
    /// <summary>The helper is running and will install the update; the tray must exit now.</summary>
    Started,

    /// <summary>The UAC prompt was declined; nothing changed.</summary>
    Declined,

    /// <summary>The helper could not be started, or the package could not be handed over.</summary>
    Failed
}

/// <summary>How handing an update to the elevated helper ended.</summary>
public sealed record HandoffOutcome(HandoffResult Result, string Message);

/// <summary>
/// Hands a verified package to the elevated helper, <c>service-admin update &lt;transfer&gt; &lt;tray pid&gt;</c>:
/// copies it to a transfer file (<see cref="UpdateTransfer"/>), starts the helper, which shows the
/// one UAC prompt, and once it runs starts the relauncher that brings the tray back afterwards.
/// The helper waits for the tray process to exit before msiexec starts, so the caller exits the
/// tray on <see cref="HandoffResult.Started"/>. A declined prompt starts nothing, and the transfer
/// file is removed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateHandoff
{
    private readonly Func<string, string[], ServiceAdminStart> _startHelper;
    private readonly Func<int, string, bool> _startRelauncher;
    private readonly Func<string, string> _writeTransfer;
    private readonly ILogger _logger;

    /// <param name="startHelper">Starts the elevated helper with an action and arguments, without waiting (<see cref="ServiceAdminClient.Start"/>).</param>
    /// <param name="startRelauncher">Starts the relauncher for a helper process id and the exe to start (<see cref="UpdateRelauncher.Start"/>).</param>
    /// <param name="writeTransfer">Copies the package to a transfer file and returns its path (<see cref="UpdateTransfer.WriteTempFile"/>).</param>
    public UpdateHandoff(
        Func<string, string[], ServiceAdminStart> startHelper,
        Func<int, string, bool> startRelauncher,
        Func<string, string> writeTransfer,
        ILogger logger)
    {
        _startHelper = startHelper ?? throw new ArgumentNullException(nameof(startHelper));
        _startRelauncher = startRelauncher ?? throw new ArgumentNullException(nameof(startRelauncher));
        _writeTransfer = writeTransfer ?? throw new ArgumentNullException(nameof(writeTransfer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static UpdateHandoff ForThisProcess(ServiceAdminClient admin, ILogger logger) =>
        new(admin.Start, UpdateRelauncher.Start, source => UpdateTransfer.WriteTempFile(source), logger);

    /// <param name="packagePath">The verified package.</param>
    /// <param name="installedExe">The installed exe the relauncher starts again.</param>
    /// <param name="trayProcessId">This process, which the helper waits for.</param>
    public HandoffOutcome Run(string packagePath, string installedExe, int trayProcessId)
    {
        string transfer;
        try
        {
            transfer = _writeTransfer(packagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HandoffOutcome(HandoffResult.Failed, $"The package could not be handed to the administrative helper: {exception.Message}");
        }

        var started = _startHelper(ServiceAdminCommand.UpdateAction,
            [transfer, trayProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (started.Process is not { } helper)
        {
            UpdateTransfer.Delete(transfer);
            return started.Outcome.Cancelled
                ? new HandoffOutcome(HandoffResult.Declined, "The administrator prompt was declined.")
                : new HandoffOutcome(HandoffResult.Failed, started.Outcome.Message);
        }

        using (helper)
        {
            if (!_startRelauncher(helper.Id, installedExe))
            {
                _logger.LogWarning("The tray could not arrange to start again after the update; start it from the Start menu afterwards.");
            }
        }

        return new HandoffOutcome(HandoffResult.Started, "The administrative helper is installing the update.");
    }
}
