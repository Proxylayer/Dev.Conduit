namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>A throwaway directory that removes itself when the test finishes.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Proxylayer.DevConduit.Tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file must never turn a passing test red.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
