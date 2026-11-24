using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// <c>Dev.Conduit.exe certs list</c>, <c>certs revoke &lt;serial&gt;</c> and
/// <c>certs revoke --enrollment &lt;id&gt;</c>: the enrollments this server has created, one per
/// export, each with its latest client certificate, and revoking one. Revoking by serial
/// revokes the whole enrollment that serial belongs to, so every renewal made from it is
/// refused by <c>/cert</c> too. The certificate folder is for administrators, so this runs
/// elevated, or as the helper's <c>service-admin revoke</c>. <c>certs untrust</c> removes from
/// Windows trust the server certificates this server added there (<see cref="ServerSelfTrust"/>);
/// from an ordinary prompt it asks for elevation through <c>service-admin untrust</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class CertsCommand
{
    public const string Verb = "certs";
    public const string ListAction = "list";
    public const string RevokeAction = "revoke";
    public const string EnrollmentOption = "--enrollment";
    public const string UntrustAction = "untrust";

    public static int Run(string[] args)
    {
        var action = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        return action switch
        {
            ListAction => (int)List(args[1..], SettingsFiles.ForThisComputer(), Console.Out),
            RevokeAction when args.Length >= 3 && string.Equals(args[1], EnrollmentOption, StringComparison.OrdinalIgnoreCase) =>
                (int)RevokeEnrollment(args[2], args[3..], SettingsFiles.ForThisComputer(), Console.Out),
            RevokeAction when args.Length >= 2 => (int)Revoke(args[1], args[2..], SettingsFiles.ForThisComputer(), Console.Out),
            UntrustAction => RunUntrust(args[1..]),
            _ => Usage()
        };
    }

    /// <summary>A serial number as the ledger and the helper accept it: 2 to 64 hex digits.</summary>
    public static bool IsAcceptedSerial(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) && Serial().IsMatch(serial.Trim());

    /// <summary>Revokes the enrollment <paramref name="serial"/> belongs to.</summary>
    public static ServiceAdminExitCode Revoke(string serial, string[] configurationArguments, SettingsFiles settingsFiles, TextWriter output)
    {
        if (!IsAcceptedSerial(serial))
        {
            output.WriteLine($"'{serial}' is not a certificate serial number.");
            return ServiceAdminExitCode.InvalidArguments;
        }

        return Change(
            configurationArguments,
            settingsFiles,
            output,
            authority => authority.Revoke(serial),
            $"No client certificate with serial {serial} was issued by this server.");
    }

    /// <summary>Revokes an enrollment by its id.</summary>
    public static ServiceAdminExitCode RevokeEnrollment(string enrollment, string[] configurationArguments, SettingsFiles settingsFiles, TextWriter output)
    {
        if (!ClientEnrollmentId.IsValid(enrollment?.Trim()))
        {
            output.WriteLine($"'{enrollment}' is not an enrollment id: 32 hex digits, as certs list shows it.");
            return ServiceAdminExitCode.InvalidArguments;
        }

        return Change(
            configurationArguments,
            settingsFiles,
            output,
            authority => authority.RevokeEnrollment(ClientEnrollmentId.Normalize(enrollment!)),
            $"No enrollment {enrollment} was created by this server.");
    }

    /// <summary>
    /// Removes every certificate this server added to Windows trust for itself, as its record
    /// lists them; nothing else in the store is touched. A running service adds the ones in use
    /// back at its next check, so this is for a server being retired: stop the service, or run
    /// it just before removing the Server role.
    /// </summary>
    public static ServiceAdminExitCode Untrust(string[] configurationArguments, SettingsFiles settingsFiles, TextWriter output)
    {
        if (!CommandConfiguration.TryBuild(configurationArguments, settingsFiles, out var configuration, out var error))
        {
            output.WriteLine(error);
            return ServiceAdminExitCode.UntrustFailed;
        }

        var devConduit = configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>() ?? new DevConduitOptions();
        if (!ServerSelfTrust.TryParseStore(devConduit.Server.TrustStore, out var location, out var name, out var problem))
        {
            output.WriteLine(problem);
            return ServiceAdminExitCode.UntrustFailed;
        }

        var record = ServerSelfTrust.RecordPath(ServerCertificateStore.ForDataRoot(devConduit.ResolveDataRoot()));
        if (location is null || !File.Exists(record))
        {
            output.WriteLine($"This server has added no certificates to Windows trust: there is no record at {record}.");
            return ServiceAdminExitCode.Success;
        }

        var trust = new RootStoreTrust(record, name, location.Value);
        try
        {
            var removed = trust.RemoveAll();
            output.WriteLine(removed.Count == 0
                ? $"{trust.StoreDescription}: nothing to remove."
                : $"{trust.StoreDescription}: removed {string.Join(", ", removed)}.");
            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException
                                              or IOException or System.Text.Json.JsonException)
        {
            output.WriteLine($"{trust.StoreDescription} could not be changed: {exception.Message}");
            return ServiceAdminExitCode.UntrustFailed;
        }
    }

    /// <summary>Writes every enrollment, newest first, with its state and its latest certificate.</summary>
    public static ServiceAdminExitCode List(string[] configurationArguments, SettingsFiles settingsFiles, TextWriter output)
    {
        if (!TryAuthority(configurationArguments, settingsFiles, output, out var authority))
        {
            return ServiceAdminExitCode.NotFound;
        }

        try
        {
            var ledger = authority.ReadIssued();
            var current = authority.CurrentAuthorityThumbprint();
            if (ledger.Records.Count == 0)
            {
                output.WriteLine("No client certificates have been issued.");
            }

            foreach (var enrollment in ledger.Enrollments.OrderByDescending(enrollment => enrollment.CreatedAt))
            {
                var certificates = ledger.CertificatesOf(enrollment.Id);
                var state = enrollment.Revoked
                    ? $"revoked {enrollment.RevokedAt:yyyy-MM-dd} ({enrollment.RevokedReason})"
                    : "active";
                var signer = string.Equals(enrollment.Authority, current, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : $"  retired authority {enrollment.Authority} for {enrollment.Hostname}";
                output.WriteLine($"Enrollment {enrollment.Id}  created {enrollment.CreatedAt:yyyy-MM-dd}  {state}  {enrollment.Subject}{signer}");

                if (certificates.Count > 0)
                {
                    var latest = certificates[^1];
                    output.WriteLine(
                        $"  latest {latest.Serial}  issued {latest.IssuedAt:yyyy-MM-dd}  until {latest.NotAfter:yyyy-MM-dd}  "
                        + $"{certificates.Count} certificate{(certificates.Count == 1 ? string.Empty : "s")} in all");
                }
            }

            foreach (var record in ledger.Records.Where(record => record.Enrollment is null).OrderBy(record => record.IssuedAt))
            {
                output.WriteLine(
                    $"No enrollment  {record.Serial}  issued {record.IssuedAt:yyyy-MM-dd}  until {record.NotAfter:yyyy-MM-dd}  "
                    + $"{(record.Revoked ? "revoked" : "not accepted: issued before enrollments")}  {record.Subject}");
            }

            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
                                              or InvalidOperationException or System.Text.Json.JsonException)
        {
            output.WriteLine($"The certificate record could not be read: {exception.Message}");
            return ServiceAdminExitCode.UnexpectedError;
        }
    }

    private static ServiceAdminExitCode Change(
        string[] configurationArguments,
        SettingsFiles settingsFiles,
        TextWriter output,
        Func<ServerCertificateAuthority, EnrollmentRevocation?> revoke,
        string notFound)
    {
        if (!TryAuthority(configurationArguments, settingsFiles, output, out var authority))
        {
            return ServiceAdminExitCode.NotFound;
        }

        try
        {
            if (revoke(authority) is not { } revocation)
            {
                output.WriteLine(notFound);
                return ServiceAdminExitCode.NotFound;
            }

            output.WriteLine(revocation.Enrollment is null
                ? $"Revoked {string.Join(", ", revocation.Serials)}, issued before enrollments. /cert refuses it from now on."
                : $"Revoked enrollment {revocation.Enrollment}: {string.Join(", ", revocation.Serials)}. /cert refuses every certificate of it, "
                  + "including any renewal, from now on.");
            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
                                              or InvalidOperationException or System.Text.Json.JsonException)
        {
            output.WriteLine($"The certificate record could not be changed: {exception.Message}");
            return ServiceAdminExitCode.UnexpectedError;
        }
    }

    private static bool TryAuthority(string[] configurationArguments, SettingsFiles settingsFiles, TextWriter output, out ServerCertificateAuthority authority)
    {
        authority = null!;
        if (!CommandConfiguration.TryBuild(configurationArguments, settingsFiles, out var configuration, out var error))
        {
            output.WriteLine(error);
            return false;
        }

        var devConduit = configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>() ?? new DevConduitOptions();
        var store = ServerCertificateStore.ForDataRoot(devConduit.ResolveDataRoot());
        if (!Directory.Exists(store.Directory))
        {
            output.WriteLine($"There is no certificate folder at {store.Directory}: this is not a server with DevConduit:Server:Hostname set, or DevConduit:DataRoot differs.");
            return false;
        }

        authority = new ServerCertificateAuthority(store, TimeProvider.System, NullLogger.Instance);
        return true;
    }

    /// <summary><c>certs untrust</c>: here when elevated, otherwise through the elevated helper, which takes no configuration arguments.</summary>
    private static int RunUntrust(string[] configurationArguments)
    {
        if (IsElevated())
        {
            return (int)Untrust(configurationArguments, SettingsFiles.ForThisComputer(), Console.Out);
        }

        if (configurationArguments.Length > 0)
        {
            Console.Error.WriteLine($"{Verb} {UntrustAction} with configuration arguments runs in this process, which needs an elevated prompt.");
            return (int)ServiceAdminExitCode.NotElevated;
        }

        Console.WriteLine("Asking for administrator rights to remove this server's certificates from Windows trust...");
        var outcome = ServiceAdminClient.ForThisProcess().RunAsync(ServiceAdminCommand.UntrustAction).GetAwaiter().GetResult();
        if (outcome.Cancelled)
        {
            Console.Error.WriteLine("The elevation prompt was declined; nothing was changed.");
            return (int)ServiceAdminExitCode.NotElevated;
        }

        Console.WriteLine(outcome.Succeeded ? "Removed the server certificates this app added to Windows trust." : outcome.Message);
        return outcome.Succeeded ? 0 : (int)ServiceAdminExitCode.UntrustFailed;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Usage()
    {
        Console.Error.WriteLine($"Usage: Dev.Conduit.exe {Verb} {ListAction}");
        Console.Error.WriteLine($"       Dev.Conduit.exe {Verb} {RevokeAction} <serial>                 revokes the serial's whole enrollment");
        Console.Error.WriteLine($"       Dev.Conduit.exe {Verb} {RevokeAction} {EnrollmentOption} <id>");
        Console.Error.WriteLine($"       Dev.Conduit.exe {Verb} {UntrustAction}                          removes this server's certificates from Windows trust");
        return (int)ServiceAdminExitCode.InvalidArguments;
    }

    [GeneratedRegex(@"\A[0-9A-Fa-f]{2,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Serial();
}
