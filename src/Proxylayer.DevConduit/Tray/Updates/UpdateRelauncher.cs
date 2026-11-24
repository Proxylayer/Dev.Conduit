using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Proxylayer.DevConduit.Tray.Updates;

/// <summary>
/// Brings the tray back after an update. The tray must not hold Dev.Conduit.exe while msiexec
/// replaces it, and neither may the elevated helper, which is the same exe; so the helper starts
/// msiexec and exits, the tray exits, and this small unelevated, hidden Windows PowerShell process
/// is what waits: for the helper to exit, then for the msiexec it started (found by its parent
/// process id, which stays recorded after the parent ends), then starts
/// <c>"&lt;install folder&gt;\Dev.Conduit.exe" tray</c> as the signed-in user. It polls with
/// <c>Get-Process</c>, which needs no handle to the elevated processes, and gives up after an hour.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateRelauncher
{
    /// <summary>Windows PowerShell in the system folder, found through the known folder rather than PATH.</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The script for waiting on <paramref name="helperProcessId"/> and then starting <paramref name="exePath"/>.</summary>
    public static string Script(int helperProcessId, string exePath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(helperProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!Path.IsPathFullyQualified(exePath))
        {
            throw new ArgumentException($"'{exePath}' is not a fully qualified path.", nameof(exePath));
        }

        var id = helperProcessId.ToString(CultureInfo.InvariantCulture);
        var exe = exePath.Replace("'", "''", StringComparison.Ordinal);
        return string.Join("\n",
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$deadline = (Get-Date).AddHours(1)",
            $"while ((Get-Process -Id {id}) -and (Get-Date) -lt $deadline) {{ Start-Sleep -Milliseconds 500 }}",
            $"$installers = @(Get-CimInstance Win32_Process -Filter \"ParentProcessId={id} AND Name='msiexec.exe'\" | ForEach-Object ProcessId)",
            "foreach ($installer in $installers) { while ((Get-Process -Id $installer) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 } }",
            $"Start-Process -FilePath '{exe}' -ArgumentList 'tray'");
    }

    /// <summary>The PowerShell arguments: no profile, no prompts, hidden, the script encoded so no quoting can break it.</summary>
    public static string Arguments(int helperProcessId, string exePath) =>
        "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(helperProcessId, exePath)));

    /// <summary>Starts the relauncher; false when it could not be started.</summary>
    public static bool Start(int helperProcessId, string exePath)
    {
        try
        {
            var info = new ProcessStartInfo(PowerShellPath, Arguments(helperProcessId, exePath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
