using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Ui;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// The entry point for <c>Dev.Conduit.exe tray</c>: one notification-area app per user,
/// running its message loop on an STA thread of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayProgram
{
    public static int Run(string[] args)
    {
        if (TrayConsole.IsShared())
        {
            return TrayConsole.RelaunchDetached([ServiceLaunch.TrayVerb, .. args]);
        }

        TrayConsole.Release();

        var exitCode = 0;
        var thread = new Thread(() => exitCode = RunMessageLoop()) { Name = "Dev.Conduit tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunMessageLoop()
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalRunningInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        using var context = new TrayApplicationContext(instance);
        Application.Run(context);
        return 0;
    }
}
