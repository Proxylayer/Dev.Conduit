using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.DataProtection;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Puts the ASP.NET Core Data Protection key ring, which signs and encrypts the sign-in
/// cookie and the OpenID Connect correlation state, in a fixed place under the data root.
/// Left to its defaults the ring lives in the running account's profile under user-scope
/// DPAPI and is keyed to the content root, so a password reset, a different service account
/// or a zip extracted to a new folder each leave it unreadable and sign everyone out.
/// </summary>
public sealed record DataProtectionSetup(string KeyDirectory, string Protection, string? AccessWarning)
{
    /// <summary>The application discriminator, fixed so the keys do not depend on the install folder.</summary>
    public const string ApplicationName = "Dev.Conduit";

    /// <summary>The key ring's directory under the data root.</summary>
    public const string KeyDirectoryName = "keys";

    public static DataProtectionSetup Register(WebApplicationBuilder builder, DevConduitOptions devConduit)
    {
        var keyDirectory = new DirectoryInfo(Path.Combine(devConduit.ResolveDataRoot(), KeyDirectoryName));

        var dataProtection = builder.Services
            .AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(keyDirectory);

        if (!OperatingSystem.IsWindows())
        {
            return new DataProtectionSetup(
                keyDirectory.FullName,
                "no encryption at rest (DPAPI is Windows-only); the directory's own permissions protect the keys",
                AccessWarning: null);
        }

        // Machine scope survives password resets and account changes. The price is that any
        // local account able to read the key files can decrypt them, so the directory is
        // limited to SYSTEM, Administrators and this process's account before a key is written.
        dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        var accessWarning = KeyDirectoryAccess.Restrict(keyDirectory);

        var protection = accessWarning is null
            ? $"machine-scope DPAPI, directory limited to SYSTEM, Administrators and {CurrentAccountName()}"
            : "machine-scope DPAPI, directory access NOT limited (see the warning above)";

        return new DataProtectionSetup(keyDirectory.FullName, protection, accessWarning);
    }

    public void Report(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dev.Conduit.Startup");

        if (AccessWarning is not null)
        {
            logger.LogWarning("{Warning}", AccessWarning);
        }

        logger.LogInformation("Data Protection keys are stored under {KeyDirectory}, protected with {Protection}.", KeyDirectory, Protection);
    }

    [SupportedOSPlatform("windows")]
    private static string CurrentAccountName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}
