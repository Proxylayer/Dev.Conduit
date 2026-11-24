using System.Runtime.Versioning;
using System.Text.Json;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>A certificate the server keeps, and the file holding it with its key.</summary>
/// <param name="File">The file name under the store's directory, encrypted with machine-scope DPAPI.</param>
public sealed record StoredCertificate(string File, string Thumbprint, string Hostname, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
{
    public ServerCertificateFacts Facts => new(Thumbprint, Hostname, NotBefore, NotAfter);
}

/// <summary>Which stored certificate is which: <c>index.json</c> in the store's directory.</summary>
public sealed record ServerCertificateIndex(StoredCertificate? Current, StoredCertificate? Next, StoredCertificate? ClientAuthority)
{
    public static ServerCertificateIndex Empty { get; } = new(null, null, null);
}

/// <summary>
/// The server's certificate directory, <c>{DataRoot}\certs</c>: the current and next server
/// certificates and the client-issuing authority, each a PKCS#12 blob encrypted with
/// machine-scope DPAPI, an index naming them, and the record of issued client certificates.
/// The directory gets the access list of the Data Protection key directory (SYSTEM,
/// Administrators and the account the server runs as), since machine-scope DPAPI lets any
/// local account that can read a file decrypt it. No Windows certificate store is used.
/// The service and the elevated export command share it, so every change happens under
/// <see cref="Lock"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerCertificateStore
{
    public const string DirectoryName = "certs";
    public const string IndexFileName = "index.json";
    public const string IssuedFileName = "issued.json";

    private const string LockFileName = ".lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(100);

    public ServerCertificateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!System.IO.Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException($"'{directory}' is not a fully qualified path.", nameof(directory));
        }

        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public string IndexPath => System.IO.Path.Combine(Directory, IndexFileName);

    public string IssuedPath => System.IO.Path.Combine(Directory, IssuedFileName);

    public static ServerCertificateStore ForDataRoot(string dataRoot) => new(System.IO.Path.Combine(dataRoot, DirectoryName));

    /// <summary>
    /// Creates the directory with its restricted access list, or tightens an existing one.
    /// Returns a warning when that could not be done.
    /// </summary>
    public string? Prepare() => KeyDirectoryAccess.Restrict(new DirectoryInfo(Directory));

    /// <summary>
    /// Holds the store for one process at a time, across processes: an exclusive handle on a
    /// lock file. Waits up to thirty seconds while another process holds it; any other failure
    /// to open the lock file is reported at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store's directory does not exist: it was never prepared.</exception>
    /// <exception cref="TimeoutException">Another process held the store for the whole wait.</exception>
    public IDisposable Lock()
    {
        var path = System.IO.Path.Combine(Directory, LockFileName);
        var deadline = DateTime.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw NotPrepared(exception);
            }
            catch (IOException exception) when (AtomicFile.IsSharingViolation(exception) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockRetry);
            }
            catch (IOException exception) when (AtomicFile.IsSharingViolation(exception))
            {
                throw new TimeoutException($"The certificate store {Directory} is in use by another process.", exception);
            }
        }
    }

    public ServerCertificateIndex ReadIndex() => ReadJson<ServerCertificateIndex>(IndexPath) ?? ServerCertificateIndex.Empty;

    public void WriteIndex(ServerCertificateIndex index) => WriteJson(IndexPath, index);

    /// <summary>
    /// The enrollments and issued certificates. A file holding only a list of certificates,
    /// from before enrollments, is read as certificates without an enrollment.
    /// </summary>
    public IssuedCertificateLedger ReadIssued()
    {
        if (!File.Exists(IssuedPath))
        {
            return new IssuedCertificateLedger([]);
        }

        var text = File.ReadAllText(IssuedPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new IssuedCertificateLedger([]);
        }

        if (text.TrimStart().StartsWith('['))
        {
            return new IssuedCertificateLedger(JsonSerializer.Deserialize<List<IssuedCertificateRecord>>(text, StorageJson.Options) ?? []);
        }

        var document = JsonSerializer.Deserialize<IssuedCertificateLedgerDocument>(text, StorageJson.Options);
        return new IssuedCertificateLedger(document?.Certificates ?? [], document?.Enrollments ?? []);
    }

    public void WriteIssued(IssuedCertificateLedger ledger) => WriteJson(IssuedPath, ledger.ToDocument());

    /// <summary>Stores a PKCS#12 blob and returns the file name it went to.</summary>
    public string SavePkcs12(string kind, string thumbprint, byte[] pkcs12)
    {
        var fileName = $"{kind}-{thumbprint}.pfx.dpapi";
        DpapiProtectedFile.Write(System.IO.Path.Combine(Directory, fileName), pkcs12);
        return fileName;
    }

    /// <summary>The PKCS#12 blob of a stored certificate.</summary>
    public byte[] LoadPkcs12(StoredCertificate stored) => DpapiProtectedFile.Read(PathOf(stored));

    /// <summary>Deletes a stored certificate's file, if it is still there.</summary>
    public void Delete(StoredCertificate stored)
    {
        var path = PathOf(stored);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private InvalidOperationException NotPrepared(Exception inner) =>
        new($"The certificate store {Directory} is not prepared: it does not exist yet. Start the server once with DevConduit:Server:Hostname set.", inner);

    private string PathOf(StoredCertificate stored)
    {
        // The index is written only by this class, but a name with a path in it is refused anyway.
        if (System.IO.Path.GetFileName(stored.File) != stored.File)
        {
            throw new InvalidDataException($"The certificate index in {Directory} names '{stored.File}', which is not a file name.");
        }

        return System.IO.Path.Combine(Directory, stored.File);
    }

    private static T? ReadJson<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, StorageJson.Options);
    }

    private static void WriteJson<T>(string path, T value) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, StorageJson.Options));
}
