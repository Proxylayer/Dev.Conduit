using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Puts the server's own current and next certificates into Windows trust, so a browser on the
/// server, and the tray's server-role calls, trust <c>https://&lt;hostname&gt;:&lt;port&gt;</c>.
/// Once a certificate is promoted, replaced or expires, or the host name changes, the ones this
/// app added are taken out again. The certificates added are recorded in <c>root-store.json</c>
/// in the server's certificate folder, through <see cref="RootStoreTrust"/>, and only those
/// are ever removed. Each must pass <see cref="ServerCertificateRules"/> for the host name.
/// The service runs as LocalSystem and can write <c>LocalMachine\Root</c>; a console server run
/// unelevated cannot, which is logged once as a warning and otherwise ignored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerSelfTrust
{
    public const string RecordFileName = "root-store.json";

    /// <summary>The setting naming the store, <c>LocalMachine\Root</c> by default, or <c>None</c> to leave Windows trust alone.</summary>
    public const string StoreKey = "DevConduit:Server:TrustStore";

    public const string DefaultStore = @"LocalMachine\Root";
    public const string NoStore = "None";

    private readonly RootStoreTrust? _trust;
    private readonly ILogger _logger;
    private bool _warned;

    /// <param name="trust">The store and record to use, or null to leave Windows trust alone.</param>
    public ServerSelfTrust(RootStoreTrust? trust, ILogger logger)
    {
        _trust = trust;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The store written, or null when self-trust is off.</summary>
    public string? StoreDescription => _trust?.StoreDescription;

    /// <summary>The record of what was added, in the server's certificate folder.</summary>
    public static string RecordPath(ServerCertificateStore store) => Path.Combine(store.Directory, RecordFileName);

    /// <summary>
    /// Self-trust for <paramref name="store"/>'s certificates in the store <paramref name="setting"/>
    /// (<see cref="StoreKey"/>) names. Off when it says <see cref="NoStore"/>, and off with an
    /// error logged when it names no usable store: a bad setting never stops the server serving.
    /// </summary>
    public static ServerSelfTrust Create(ServerCertificateStore store, string? setting, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        if (!TryParseStore(setting, out var location, out var name, out var problem))
        {
            logger.LogError("{Problem} This server's certificates are not added to Windows trust.", problem);
            return new ServerSelfTrust(null, logger);
        }

        return new ServerSelfTrust(location is null ? null : new RootStoreTrust(RecordPath(store), name, location.Value), logger);
    }

    /// <summary>
    /// Reads <see cref="StoreKey"/>: blank for <see cref="DefaultStore"/>, <see cref="NoStore"/>
    /// for none (a null <paramref name="location"/>), else <c>Location\Name</c>.
    /// <c>CurrentUser\Root</c> is refused: Windows asks the user to confirm every certificate added there.
    /// </summary>
    public static bool TryParseStore(string? setting, out StoreLocation? location, out StoreName name, out string problem)
    {
        location = null;
        name = StoreName.Root;
        problem = string.Empty;

        var value = string.IsNullOrWhiteSpace(setting) ? DefaultStore : setting.Trim();
        if (string.Equals(value, NoStore, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = value.Split('\\');
        if (parts.Length != 2
            || parts.Any(part => part.Length == 0 || !char.IsLetter(part[0]))
            || !Enum.TryParse<StoreLocation>(parts[0], ignoreCase: true, out var parsedLocation)
            || !Enum.TryParse<StoreName>(parts[1], ignoreCase: true, out var parsedName)
            || !Enum.IsDefined(parsedLocation)
            || !Enum.IsDefined(parsedName))
        {
            problem = $"{StoreKey} ('{value}') is not a certificate store: give {DefaultStore}, {NoStore}, or Location\\Name such as CurrentUser\\AddressBook.";
            return false;
        }

        if (parsedLocation == StoreLocation.CurrentUser && parsedName == StoreName.Root)
        {
            problem = $@"{StoreKey} cannot be CurrentUser\Root: Windows asks to confirm every certificate added there. Use {DefaultStore}.";
            return false;
        }

        location = parsedLocation;
        name = parsedName;
        return true;
    }

    /// <summary>
    /// Trusts <paramref name="set"/>'s published certificates, current and next, and removes the
    /// ones this app added that are no longer among them. Returns what changed, or null when
    /// self-trust is off or the store could not be changed (logged, the first time as a warning).
    /// </summary>
    public RootStoreChange? Synchronize(ServerCertificateSet set, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (_trust is null)
        {
            return null;
        }

        try
        {
            var change = _trust.Synchronize(set.Hostname, set.Published, now, removeOtherHostnames: true);
            if (change.Added.Count > 0 || change.Removed.Count > 0)
            {
                _logger.LogInformation(
                    "{Store}: trusts this server's certificates for {Hostname}; added {Added}, removed {Removed} (replaced, expired or for another host name).",
                    _trust.StoreDescription,
                    set.Hostname,
                    change.Added.Count == 0 ? "none" : string.Join(", ", change.Added),
                    change.Removed.Count == 0 ? "none" : string.Join(", ", change.Removed));
            }

            _warned = false;
            return change;
        }
        catch (Exception exception) when (exception is CryptographicException or UnauthorizedAccessException)
        {
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning(
                    "This server's certificate for {Hostname} could not be added to {Store} ({Reason}). The service, which runs as "
                    + "LocalSystem, adds it; run this console elevated for a browser on this machine to trust https://{Hostname}.",
                    set.Hostname,
                    _trust.StoreDescription,
                    exception.Message,
                    set.Hostname);
            }

            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            _logger.LogError(exception, "This server's certificates were not added to {Store}.", _trust.StoreDescription);
            return null;
        }
    }

    /// <summary>Removes every certificate this app added for the server. Returns their thumbprints; empty when self-trust is off.</summary>
    /// <exception cref="CryptographicException">The store could not be opened for writing.</exception>
    public IReadOnlyList<string> RemoveAll() => _trust?.RemoveAll() ?? [];
}
