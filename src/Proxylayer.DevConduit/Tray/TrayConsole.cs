using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// The exe is a console program, so the console and service runs keep their console. The
/// tray has no use for one: started from Explorer or the Run key it gets a console of its
/// own, which it lets go at once; started from a terminal it shares the terminal's console,
/// so it starts a detached copy of itself and exits, handing the prompt back.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayConsole
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    /// <summary>True when another process, such as a shell, is attached to this process's console.</summary>
    public static bool IsShared()
    {
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            return false;
        }

        var processes = new uint[4];
        return GetConsoleProcessList(processes, (uint)processes.Length) > 1;
    }

    /// <summary>Starts this exe again with the same arguments and a hidden console of its own.</summary>
    public static int RelaunchDetached(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        return process is null ? 1 : 0;
    }

    /// <summary>
    /// Detaches from the console and clears the standard handles, so anything that still
    /// writes to the console writes nowhere rather than to a closed handle.
    /// </summary>
    public static void Release()
    {
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            return;
        }

        FreeConsole();
        SetStdHandle(StdInputHandle, IntPtr.Zero);
        SetStdHandle(StdOutputHandle, IntPtr.Zero);
        SetStdHandle(StdErrorHandle, IntPtr.Zero);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);
}
