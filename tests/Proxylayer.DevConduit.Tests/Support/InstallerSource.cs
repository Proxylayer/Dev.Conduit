using System.Xml.Linq;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>The WiX source of the MSI, found by walking up from the test assembly to the solution folder.</summary>
internal static class InstallerSource
{
    public static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Proxylayer.DevConduit.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds Proxylayer.DevConduit.sln.");
    }

    /// <summary>The root <c>Wix</c> element of <c>installer/Package.wxs</c>, holding the package and its fragments.</summary>
    public static XElement Document()
    {
        var wxs = Path.Combine(RepositoryRoot(), "installer", "Package.wxs");
        Assert.True(File.Exists(wxs), wxs);

        var root = XDocument.Load(wxs).Root;
        Assert.NotNull(root);
        return root;
    }

    /// <summary>The <c>Package</c> element of <c>installer/Package.wxs</c>.</summary>
    public static XElement Package()
    {
        var package = Document().Element(Wix + "Package");
        Assert.NotNull(package);
        return package;
    }
}
