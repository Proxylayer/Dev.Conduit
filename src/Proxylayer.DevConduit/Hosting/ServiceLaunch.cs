using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>The ways a launch can run.</summary>
public enum LaunchKind
{
    /// <summary>The web application in the console, the default.</summary>
    Console,

    /// <summary>The Windows service host: a lifecycle verb or a start by the service control manager.</summary>
    ServiceHost,

    /// <summary>The notification-area app, <c>tray</c>.</summary>
    Tray,

    /// <summary>The elevated helper the tray starts for administrative actions, <c>service-admin</c>.</summary>
    ServiceAdmin,

    /// <summary>Writes a <c>.conduit</c> for a client, <c>export</c>. Server role.</summary>
    Export,

    /// <summary>Applies a server's <c>.conduit</c> to this workstation, <c>import</c>.</summary>
    Import,

    /// <summary>Lists or revokes issued client certificates, <c>certs</c>. Server role.</summary>
    Certificates
}

/// <summary>
/// Decides where a launch goes. The <c>tray</c> and <c>service-admin</c> verbs go to the tray
/// and its elevated helper; <c>export</c>, <c>import</c> and <c>certs</c> to the certificate
/// commands; the service lifecycle verbs and a start by the service control
/// manager go to the service host; every other command line runs the web application exactly
/// as before.
/// </summary>
public static class ServiceLaunch
{
    /// <summary>The first-argument verbs handed to the service host, matched case-insensitively.</summary>
    public static readonly IReadOnlySet<string> Verbs =
        new HashSet<string>(["install", "uninstall", "start", "stop", "help"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lifecycle verbs that register or remove the service, which the Dev.Conduit MSI
    /// owns while it is installed.
    /// </summary>
    public static readonly IReadOnlySet<string> RegistrationVerbs =
        new HashSet<string>(["install", "uninstall"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The first argument that starts the tray app.</summary>
    public const string TrayVerb = "tray";

    /// <summary>The first argument that starts the elevated helper. Not meant to be typed.</summary>
    public const string ServiceAdminVerb = "service-admin";

    /// <summary>The first argument that writes a <c>.conduit</c>.</summary>
    public const string ExportVerb = "export";

    /// <summary>The first argument that imports a <c>.conduit</c>.</summary>
    public const string ImportVerb = "import";

    /// <summary>The first argument that lists or revokes issued client certificates.</summary>
    public const string CertificatesVerb = "certs";

    /// <summary>Where this launch goes. The tray and certificate verbs are checked first; none is a Topshelf verb.</summary>
    public static LaunchKind Resolve(string[] args)
    {
        if (OperatingSystem.IsWindows() && IsVerb(args, TrayVerb))
        {
            return LaunchKind.Tray;
        }

        if (OperatingSystem.IsWindows() && IsVerb(args, ServiceAdminVerb))
        {
            return LaunchKind.ServiceAdmin;
        }

        if (OperatingSystem.IsWindows() && IsVerb(args, ExportVerb))
        {
            return LaunchKind.Export;
        }

        if (OperatingSystem.IsWindows() && IsVerb(args, ImportVerb))
        {
            return LaunchKind.Import;
        }

        if (OperatingSystem.IsWindows() && IsVerb(args, CertificatesVerb))
        {
            return LaunchKind.Certificates;
        }

        return UsesServiceHost(args) ? LaunchKind.ServiceHost : LaunchKind.Console;
    }

    private static bool IsVerb(string[] args, string verb) =>
        args.Length > 0 && string.Equals(args[0], verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this launch is a service lifecycle command or a service start.</summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool UsesServiceHost(string[] args) =>
        OperatingSystem.IsWindows() && (IsServiceVerb(args) || IsRunningAsService());

    /// <summary>True when the first argument is one of <see cref="Verbs"/>.</summary>
    public static bool IsServiceVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    /// <summary>
    /// True when any argument is one of <see cref="RegistrationVerbs"/>. Every argument is
    /// checked, not only the first, so no position Topshelf might read a verb from slips past.
    /// </summary>
    public static bool ChangesServiceRegistration(string[] args) => args.Any(RegistrationVerbs.Contains);

    /// <summary>
    /// True when the service control manager started this process: its parent is
    /// <c>services.exe</c> in session 0. Topshelf makes the same parent-process test
    /// when choosing between running the service and running in the console, so the two
    /// cannot disagree. <see cref="Environment.UserInteractive"/> is not used: it is also
    /// false for an SSH session or a scheduled task, which are console runs.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsRunningAsService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var parent = ParentProcess();
            return parent is not null
                && parent.SessionId == 0
                && string.Equals(parent.ProcessName, "services", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // The parent has exited or cannot be inspected; the SCM's services.exe never exits.
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Process? ParentProcess()
    {
        using var current = Process.GetCurrentProcess();

        var status = NtQueryInformationProcess(
            current.Handle,
            ProcessBasicInformationClass,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);

        if (status != 0)
        {
            return null;
        }

        return Process.GetProcessById((int)information.InheritedFromUniqueProcessId);
    }

    private const int ProcessBasicInformationClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
