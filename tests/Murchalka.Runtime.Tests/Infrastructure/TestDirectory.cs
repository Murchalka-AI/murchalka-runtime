namespace Murchalka.Runtime.Tests.Infrastructure;

internal sealed class TestDirectory : IDisposable
{
    /// <summary>Creates an isolated temporary directory for a test.</summary>
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murchalka-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Gets the temporary directory path.</summary>
    public string Path { get; }

    /// <summary>Deletes the temporary directory when it still exists.</summary>
    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        if (!OperatingSystem.IsWindows())
            foreach (var directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Prepend(Path))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
