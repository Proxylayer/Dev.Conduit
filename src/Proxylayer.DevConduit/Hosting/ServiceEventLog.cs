using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Where a Windows service run logs: the Application event log under its own source, in
/// addition to the console. A service has no console anyone reads, so without this the
/// startup banner and every failure would be lost.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceEventLog
{
    public const string SourceName = "Dev.Conduit";

    public const string LogName = "Application";

    /// <summary>
    /// Points the event log provider at this application's source. The host's defaults
    /// already add the provider but pass it only warnings and errors under a generic source;
    /// this application's own categories (the named <c>Dev.Conduit.*</c> ones and its types under
    /// <c>Proxylayer.DevConduit</c>) and the listening addresses are let through from
    /// Information, the rest of the framework stays at Warning.
    /// </summary>
    public static void Configure(ILoggingBuilder logging)
    {
        logging.AddEventLog(Apply);
        logging.AddFilter<EventLogLoggerProvider>("Dev.Conduit", LogLevel.Information);
        logging.AddFilter<EventLogLoggerProvider>("Proxylayer.DevConduit", LogLevel.Information);
        logging.AddFilter<EventLogLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
    }

    /// <summary>Logs an error before the host exists, such as a configuration problem that stops startup.</summary>
    public static void WriteFatal(string message)
    {
        using var factory = LoggerFactory.Create(logging => logging.AddEventLog(Apply));
        factory.CreateLogger("Dev.Conduit.Startup").LogError("{Message}", message);
    }

    private static void Apply(EventLogSettings settings)
    {
        settings.SourceName = SourceName;
        settings.LogName = LogName;
    }
}
