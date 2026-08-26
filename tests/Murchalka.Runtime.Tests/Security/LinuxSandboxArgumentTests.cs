using System.Diagnostics;
using Murchalka.Runtime.ModuleSupervisor.Services;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies the Linux process sandbox mount layout.</summary>
public sealed class LinuxSandboxArgumentTests
{
    /// <summary>Verifies that the private temporary filesystem does not hide explicit module mounts.</summary>
    [Fact]
    public void PrivateTempPrecedesModuleMounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "murchalka-sandbox-order");
        var content = Path.Combine(root, "bundle", "content");
        var working = Path.Combine(root, "runtime", "instances", "instance");
        var persistent = Path.Combine(root, "runtime", "state");
        var socket = Path.Combine(root, "runtime", "sockets", "module.sock");
        var start = new ProcessStartInfo();

        ProcessModuleSupervisor.AddLinuxSandboxArguments(
            start,
            Path.Combine(content, "module"),
            content,
            working,
            persistent,
            socket,
            "/usr/share/dotnet",
            shareNetwork: false);

        var arguments = start.ArgumentList.ToArray();
        var temporaryFileSystem = Array.IndexOf(arguments, "--tmpfs");
        Assert.True(temporaryFileSystem >= 0);
        Assert.Equal("/tmp", arguments[temporaryFileSystem + 1]);
        Assert.Single(arguments, argument => argument == "--tmpfs");
        Assert.True(temporaryFileSystem < FindBind(arguments, content));
        Assert.True(temporaryFileSystem < FindBind(arguments, working));
        Assert.True(temporaryFileSystem < FindBind(arguments, persistent));
        Assert.True(temporaryFileSystem < FindBind(arguments, Path.GetDirectoryName(socket)!));
        var capabilityDrop = Array.IndexOf(arguments, "--cap-drop");
        Assert.True(capabilityDrop >= 0);
        Assert.Equal("ALL", arguments[capabilityDrop + 1]);
    }

    /// <summary>Verifies that the rootless launcher creates an empty outer network namespace without granting container capabilities.</summary>
    [Fact]
    public void RootlessNetworkLauncherWrapsBubblewrap()
    {
        var start = new ProcessStartInfo { FileName = "/usr/bin/bwrap" };

        ProcessModuleSupervisor.ConfigureLinuxNetworkNamespaceLauncher(start);
        ProcessModuleSupervisor.AddLinuxSandboxArguments(
            start,
            "/bundle/module",
            "/bundle",
            "/runtime/instance",
            "/runtime/state",
            "/runtime/sockets/module.sock",
            "/usr/share/dotnet",
            shareNetwork: true);

        Assert.Equal("/usr/bin/unshare", start.FileName);
        Assert.Equal(
            ["--user", "--map-root-user", "--net", "--", "/usr/bin/bwrap"],
            start.ArgumentList.Take(5).ToArray());
        Assert.Contains("--share-net", start.ArgumentList);
        var capabilityDrop = start.ArgumentList.IndexOf("--cap-drop");
        Assert.True(capabilityDrop >= 0);
        Assert.Equal("ALL", start.ArgumentList[capabilityDrop + 1]);
    }

    private static int FindBind(string[] arguments, string source)
    {
        var fullSource = Path.GetFullPath(source);
        for (var index = 0; index < arguments.Length - 2; index++)
            if (arguments[index] is "--bind" or "--ro-bind" &&
                string.Equals(arguments[index + 1], fullSource, StringComparison.Ordinal))
                return index;
        return -1;
    }
}
