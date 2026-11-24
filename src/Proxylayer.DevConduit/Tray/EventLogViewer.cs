using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// Opens Event Viewer on the service's events: a custom view of the Application log filtered
/// to the <see cref="ServiceEventLog.SourceName"/> source, or the whole Application log when
/// the view cannot be opened.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EventLogViewer
{
    public static void Open()
    {
        try
        {
            var view = Path.Combine(Path.GetTempPath(), "Dev.Conduit-events.xml");
            File.WriteAllText(view, ViewDefinition());
            Process.Start(new ProcessStartInfo("eventvwr.exe") { UseShellExecute = true, Arguments = CommandLine.Quote($"/v:{view}") })?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true, Arguments = $"/c:{ServiceEventLog.LogName}" })?.Dispose();
        }
    }

    /// <summary>An Event Viewer custom view selecting this application's events.</summary>
    public static string ViewDefinition()
    {
        var log = SecurityElement.Escape(ServiceEventLog.LogName);
        var source = SecurityElement.Escape(ServiceEventLog.SourceName);
        return $"""
            <ViewerConfig>
              <QueryConfig>
                <QueryParams><UserQuery /></QueryParams>
                <QueryNode>
                  <Name>{source}</Name>
                  <QueryList>
                    <Query Id="0" Path="{log}">
                      <Select Path="{log}">*[System[Provider[@Name='{source}']]]</Select>
                    </Query>
                  </QueryList>
                </QueryNode>
              </QueryConfig>
            </ViewerConfig>
            """;
    }
}
