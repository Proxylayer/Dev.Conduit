namespace Proxylayer.DevConduit.Certificates;

/// <summary>What a stored server certificate is, without its key: enough to plan a rotation.</summary>
/// <param name="Thumbprint">The SHA-1 thumbprint, upper-case hex, as Windows shows it.</param>
/// <param name="Hostname">The host name the certificate was issued for.</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
public sealed record ServerCertificateFacts(string Thumbprint, string Hostname, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
{
    public bool IsValidFor(string hostname, DateTimeOffset now) =>
        string.Equals(Hostname, hostname, StringComparison.OrdinalIgnoreCase) && now >= NotBefore && now < NotAfter;
}

/// <summary>One step of a server certificate rotation.</summary>
public enum ServerCertificateStep
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Issue a new current certificate at once, discarding any next one.</summary>
    ReplaceCurrent,

    /// <summary>Issue the next certificate, published beside the current one so clients can pin it early.</summary>
    IssueNext,

    /// <summary>Serve the next certificate from now on.</summary>
    PromoteNext
}

/// <summary>A rotation step and the reason for it, for the log.</summary>
public sealed record ServerCertificatePlan(ServerCertificateStep Step, string Reason, bool HostnameChanged = false);

/// <summary>
/// Decides when the server's certificate rotates, given the clock. A certificate lives a
/// year. With less than <see cref="IssueNextWithin"/> left, the next certificate is issued and
/// published through <c>/cert</c> beside the current one; clients pin it at their next sync.
/// With less than <see cref="SwitchWithin"/> left the server switches to it, so every client
/// that synced in the thirty days between has it pinned already. A certificate that has
/// expired, is not yet valid or names another host is replaced at once.
/// </summary>
public static class ServerCertificateRotation
{
    public static readonly TimeSpan IssueNextWithin = TimeSpan.FromDays(60);
    public static readonly TimeSpan SwitchWithin = TimeSpan.FromDays(30);

    /// <summary>The single next step. Apply it and plan again until the step is <see cref="ServerCertificateStep.None"/>.</summary>
    public static ServerCertificatePlan Plan(string hostname, ServerCertificateFacts? current, ServerCertificateFacts? next, DateTimeOffset now)
    {
        var nextUsable = next is not null && next.IsValidFor(hostname, now);

        if (current is null)
        {
            return nextUsable
                ? new ServerCertificatePlan(ServerCertificateStep.PromoteNext, "there is no current certificate, and the next one is valid")
                : new ServerCertificatePlan(ServerCertificateStep.ReplaceCurrent, "there is no server certificate yet");
        }

        if (!string.Equals(current.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
        {
            return new ServerCertificatePlan(
                ServerCertificateStep.ReplaceCurrent,
                $"the host name changed from {current.Hostname} to {hostname}",
                HostnameChanged: true);
        }

        if (now < current.NotBefore || now >= current.NotAfter)
        {
            return nextUsable
                ? new ServerCertificatePlan(ServerCertificateStep.PromoteNext, $"the current certificate {current.Thumbprint} is outside its validity period")
                : new ServerCertificatePlan(ServerCertificateStep.ReplaceCurrent, $"the current certificate {current.Thumbprint} is outside its validity period and there is no valid next one");
        }

        var remaining = current.NotAfter - now;
        if (remaining < SwitchWithin)
        {
            return nextUsable
                ? new ServerCertificatePlan(ServerCertificateStep.PromoteNext, $"the current certificate expires on {current.NotAfter:yyyy-MM-dd}, less than {SwitchWithin.Days} days away")
                : new ServerCertificatePlan(ServerCertificateStep.ReplaceCurrent, $"the current certificate expires on {current.NotAfter:yyyy-MM-dd} and no next certificate was published in time");
        }

        if (remaining < IssueNextWithin && !nextUsable)
        {
            return new ServerCertificatePlan(
                ServerCertificateStep.IssueNext,
                $"the current certificate expires on {current.NotAfter:yyyy-MM-dd}, less than {IssueNextWithin.Days} days away");
        }

        return new ServerCertificatePlan(ServerCertificateStep.None, "the current certificate is valid");
    }
}
