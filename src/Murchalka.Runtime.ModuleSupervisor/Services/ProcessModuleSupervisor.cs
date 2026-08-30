using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.ModuleGateway.Listeners;
using Murchalka.Runtime.ModuleSupervisor.Internal;

namespace Murchalka.Runtime.ModuleSupervisor.Services;

/// <summary>Starts, monitors, and stops isolated out-of-process runtime modules.</summary>
public sealed class ProcessModuleSupervisor : IModuleSupervisor, IAsyncDisposable
{
    private const string LinuxNetworkIsolationEnvironmentVariable = "MURCHALKA_LINUX_NETWORK_ISOLATION";
    private const string LinuxNetworkNamespaceLauncher = "/usr/local/libexec/murchalka-netns-exec";
    private const string LinuxNetworkNamespaceIsolation = "namespace-launcher";
    private readonly RuntimePaths _paths;
    private readonly ConcurrentDictionary<InstanceId, ManagedModule> _modules = new();
    private bool _disposed;

    /// <summary>Creates a process module supervisor.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    public ProcessModuleSupervisor(RuntimePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public event EventHandler<ModuleExitedEventArgs>? ModuleExited;

    /// <inheritdoc />
    public async Task<IModuleGatewaySession> StartAsync(InstalledBundle bundle, PermissionDecision grant, ConfigurationSnapshot configuration, DependencyEndpointsSnapshot dependencies, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        var artifact = RuntimeArtifactSelector.SelectProcess(bundle.Manifest, RuntimeConstants.ProtocolVersion);
        var artifactPath = ResolveInside(bundle.ContentPath, artifact.EntryPoint);
        if (!File.Exists(artifactPath)) throw new FileNotFoundException("Selected module artifact is missing.", artifactPath);
        var instance = new InstanceId($"{SafeInstancePrefix(bundle.Manifest.Id.Value)}-{Guid.NewGuid():N}");
        var moduleRoot = Path.Combine(_paths.ModuleData, bundle.Manifest.Id.Value);
        var persistentDirectory = Path.Combine(moduleRoot, "state");
        var workingDirectory = Path.Combine(moduleRoot, "instances", instance.Value);
        Directory.CreateDirectory(persistentDirectory);
        Directory.CreateDirectory(workingDirectory);
        var socketPath = CreateSocketPath(instance);
        var listener = new ModuleGatewayListener(socketPath);
        var proofKey = RandomNumberGenerator.GetBytes(32);
        var startInfo = CreateStartInfo(artifactPath, workingDirectory, persistentDirectory, socketPath, bundle, artifact, instance, proofKey, grant);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new ModuleActivationException("process-start-failed", "Module process could not be started.");
        }
        catch (ModuleActivationException)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw new ModuleActivationException("process-start-failed", "Module process could not be started.", exception);
        }
        var managed = new ManagedModule(bundle.Manifest.Id, instance, process, listener);
        if (!_modules.TryAdd(instance, managed))
        {
            TryKill(process);
            await listener.DisposeAsync().ConfigureAwait(false);
            throw new ModuleActivationException("instance-id-collision", "Module instance id collision.");
        }
        managed.OutputDrain = DrainAsync(process.StandardOutput, CancellationToken.None);
        managed.ErrorDrain = DrainAsync(process.StandardError, CancellationToken.None, managed.ErrorTail);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(bundle.Manifest.Health.StartupTimeout);
            var accept = listener.AcceptAsync(bundle, artifact, instance, process.Id, proofKey, grant, configuration, dependencies, startup.Token);
            var exited = process.WaitForExitAsync(startup.Token);
            var completed = await Task.WhenAny(accept, exited).ConfigureAwait(false);
            if (completed == exited)
            {
                if (managed.ErrorDrain is not null) await managed.ErrorDrain.ConfigureAwait(false);
                throw new ModuleActivationException($"process-exited-before-ready:{process.ExitCode}", $"Module process exited with code {process.ExitCode} before protocol readiness: {managed.ErrorTail}.");
            }
            var session = await accept.ConfigureAwait(false);
            managed.Session = session;
            _ = MonitorExitAsync(managed);
            return session;
        }
        catch
        {
            managed.Stopping = true;
            TryKill(process);
            _modules.TryRemove(instance, out _);
            await listener.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            CryptographicOperations.ZeroMemory(proofKey);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(proofKey); }
    }

    /// <inheritdoc />
    public IModuleGatewaySession? GetSession(InstanceId instanceId) => _modules.TryGetValue(instanceId, out var module) ? module.Session : null;

    /// <inheritdoc />
    public async Task StopAsync(InstanceId instanceId, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        if (!_modules.TryGetValue(instanceId, out var module)) return;
        module.Stopping = true;
        try
        {
            if (module.Session is not null)
            {
                var drain = await module.Session.SendControlAsync(ControlMessageKind.Drain, drainTimeout, cancellationToken).ConfigureAwait(false);
                if (!drain.Succeeded) throw new InvalidOperationException($"Module drain failed: {drain.ErrorCode}.");
                var stop = await module.Session.SendControlAsync(ControlMessageKind.Stop, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (!stop.Succeeded) throw new InvalidOperationException($"Module stop failed: {stop.ErrorCode}.");
            }
            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await module.Process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            TryKill(module.Process);
            await module.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _modules.TryRemove(instanceId, out _);
            await DisposeManagedAsync(module).ConfigureAwait(false);
        }
    }

    private async Task MonitorExitAsync(ManagedModule module)
    {
        try { await module.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { return; }
        if (module.Stopping) return;
        _modules.TryRemove(module.InstanceId, out _);
        var exitCode = module.Process.ExitCode;
        ModuleExited?.Invoke(this, new ModuleExitedEventArgs(module.ModuleId, module.InstanceId, exitCode, "process-exited"));
        await DisposeManagedAsync(module).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(
        string artifactPath,
        string workingDirectory,
        string persistentDirectory,
        string socketPath,
        InstalledBundle bundle,
        RuntimeArtifact artifact,
        InstanceId instance,
        byte[] proofKey,
        PermissionDecision grant)
    {
        var dotnetRoot = ResolveDotnetRoot();
        if (OperatingSystem.IsWindows())
            throw new ModuleActivationException("windows-sandbox-unavailable", "Process modules are fail-closed on Windows until an AppContainer launcher is available.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsMacOS() ? "/usr/bin/sandbox-exec" : "/usr/bin/bwrap",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsMacOS())
        {
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(CreateMacSandboxProfile(artifactPath, bundle.ContentPath, workingDirectory, persistentDirectory, socketPath, dotnetRoot, grant, bundle.Manifest));
            start.ArgumentList.Add(artifactPath);
        }
        else
        {
            if (!File.Exists(start.FileName))
                throw new ModuleActivationException("linux-sandbox-unavailable", "Process modules require /usr/bin/bwrap and fail closed when it is unavailable.");
            var shareNetwork = HasApprovedOutboundNetwork(grant.Grant) || HasDeclaredLoopbackListener(bundle.Manifest);
            var useNamespaceLauncher = UseNetworkNamespaceLauncher();
            if (useNamespaceLauncher)
            {
                if (!File.Exists(LinuxNetworkNamespaceLauncher))
                    throw new ModuleActivationException("linux-network-sandbox-unavailable", $"Configured Linux network isolation requires {LinuxNetworkNamespaceLauncher}.");
                ConfigureLinuxNetworkNamespaceLauncher(start, shareNetwork);
            }
            AddLinuxSandboxArguments(
                start,
                artifactPath,
                bundle.ContentPath,
                workingDirectory,
                persistentDirectory,
                socketPath,
                dotnetRoot,
                shareNetwork,
                reusePrecreatedUserNamespace: useNamespaceLauncher);
        }
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment[$"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}"] = dotnetRoot;
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["MURCHALKA_SOCKET"] = socketPath;
        start.Environment["MURCHALKA_MODULE_ID"] = bundle.Manifest.Id.Value;
        start.Environment["MURCHALKA_MODULE_VERSION"] = bundle.Manifest.Version.ToString();
        start.Environment["MURCHALKA_BUNDLE_DIGEST"] = bundle.Digest;
        start.Environment["MURCHALKA_ARTIFACT_ID"] = artifact.Id;
        start.Environment["MURCHALKA_INSTANCE_ID"] = instance.Value;
        start.Environment["MURCHALKA_CAPABILITIES_DIGEST"] = CapabilityDeclarations.ComputeDigest(bundle.Manifest, bundle.ContentPath);
        start.Environment["MURCHALKA_PROOF_KEY"] = Convert.ToBase64String(proofKey);
        start.Environment["MURCHALKA_MODULE_DATA"] = persistentDirectory;
        start.Environment["MURCHALKA_INSTANCE_TEMP"] = workingDirectory;
        return start;
    }

    internal static void AddLinuxSandboxArguments(
        ProcessStartInfo start,
        string artifactPath,
        string contentPath,
        string workingDirectory,
        string persistentDirectory,
        string socketPath,
        string dotnetRoot,
        bool shareNetwork,
        bool reusePrecreatedUserNamespace = false)
    {
        start.ArgumentList.Add("--die-with-parent");
        start.ArgumentList.Add("--new-session");
        if (reusePrecreatedUserNamespace)
        {
            start.ArgumentList.Add("--unshare-ipc");
            start.ArgumentList.Add("--unshare-pid");
            start.ArgumentList.Add("--unshare-uts");
        }
        else
        {
            start.ArgumentList.Add("--unshare-all");
            if (shareNetwork) start.ArgumentList.Add("--share-net");
        }
        start.ArgumentList.Add("--cap-drop");
        start.ArgumentList.Add("ALL");
        // Bubblewrap applies mounts in argument order. Create the private /tmp first so
        // explicit bundle, state, and socket binds below remain visible when hosted there.
        start.ArgumentList.Add("--tmpfs");
        start.ArgumentList.Add("/tmp");
        AddBind(start, "--ro-bind", contentPath);
        AddBind(start, "--bind", workingDirectory);
        AddBind(start, "--bind", persistentDirectory);
        AddBind(start, "--bind", Path.GetDirectoryName(socketPath) ?? throw new InvalidDataException("The module socket has no parent directory."));
        AddBind(start, "--ro-bind", dotnetRoot);
        foreach (var systemPath in new[] { "/usr/lib", "/lib", "/lib64", "/etc/ld.so.cache", "/etc/ssl/certs", "/usr/share/zoneinfo" })
            if (File.Exists(systemPath) || Directory.Exists(systemPath)) AddBind(start, "--ro-bind", systemPath);
        start.ArgumentList.Add("--proc");
        start.ArgumentList.Add("/proc");
        start.ArgumentList.Add("--dev");
        start.ArgumentList.Add("/dev");
        start.ArgumentList.Add("--chdir");
        start.ArgumentList.Add(workingDirectory);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(artifactPath);
    }

    internal static void ConfigureLinuxNetworkNamespaceLauncher(ProcessStartInfo start, bool shareNetwork = false)
    {
        start.FileName = LinuxNetworkNamespaceLauncher;
        if (shareNetwork) start.ArgumentList.Add("--share-net");
        start.ArgumentList.Add("/usr/bin/bwrap");
    }

    private static bool UseNetworkNamespaceLauncher()
    {
        var mode = Environment.GetEnvironmentVariable(LinuxNetworkIsolationEnvironmentVariable);
        if (string.IsNullOrEmpty(mode)) return false;
        if (string.Equals(mode, LinuxNetworkNamespaceIsolation, StringComparison.Ordinal)) return true;
        throw new ModuleActivationException(
            "linux-network-sandbox-mode-invalid",
            $"Environment variable {LinuxNetworkIsolationEnvironmentVariable} has unsupported value '{mode}'.");
    }

    private static void AddBind(ProcessStartInfo start, string option, string path)
    {
        var fullPath = Path.GetFullPath(path);
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(fullPath);
        start.ArgumentList.Add(fullPath);
    }

    private static bool HasApprovedOutboundNetwork(JsonElement grant)
    {
        if (!grant.TryGetProperty("network", out var network) ||
            !network.TryGetProperty("outbound", out var outbound) ||
            outbound.ValueKind != JsonValueKind.Array)
            return false;
        return outbound.EnumerateArray().Any(rule =>
            rule.TryGetProperty("scheme", out var scheme) && scheme.ValueKind == JsonValueKind.String &&
            rule.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String &&
            rule.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() > 0);
    }

    private static bool HasDeclaredLoopbackListener(ModuleManifest manifest) =>
        manifest.Document.TryGetProperty("artifacts", out var artifacts) &&
        artifacts.TryGetProperty("runtime", out var runtimeArtifacts) &&
        runtimeArtifacts.EnumerateArray().Any(artifact =>
            artifact.TryGetProperty("requiredHostFeatures", out var features) &&
            features.EnumerateArray().Any(feature => feature.GetString() == "loopback-listener")) ||
        manifest.Document.TryGetProperty("extensions", out var extensions) &&
        extensions.TryGetProperty("dev.murchalka.client-realtime", out var realtime) &&
        realtime.TryGetProperty("loopbackListener", out var listener) &&
        listener.ValueKind == JsonValueKind.True;

    private static string CreateMacSandboxProfile(
        string artifactPath,
        string contentPath,
        string workingDirectory,
        string persistentDirectory,
        string socketPath,
        string dotnetRoot,
        PermissionDecision grant,
        ModuleManifest manifest)
    {
        static string Literal(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var logicalArtifact = Path.GetFullPath(artifactPath);
        var logicalContent = Path.GetFullPath(contentPath);
        var logicalWorking = Path.GetFullPath(workingDirectory);
        var logicalPersistent = Path.GetFullPath(persistentDirectory);
        var logicalSocket = Path.GetFullPath(socketPath);
        var canonicalContent = ResolvePhysicalPath(contentPath);
        var canonicalWorking = ResolvePhysicalPath(workingDirectory);
        var canonicalPersistent = ResolvePhysicalPath(persistentDirectory);
        var canonicalSocket = ResolvePhysicalPath(socketPath);
        var rules = new List<string>
        {
            "(version 1)",
            "(deny default)",
            "(import \"system.sb\")",
            "(deny network*)",
            "(deny process-fork)",
            $"(allow process-exec (literal {Literal(logicalArtifact)}) (literal {Literal(ResolvePhysicalPath(artifactPath))}))",
            "(allow process-info*)",
            "(allow file-read-metadata)",
            $"(allow file-read* (subpath {Literal(logicalContent)}) (subpath {Literal(canonicalContent)}) (subpath {Literal(logicalWorking)}) (subpath {Literal(canonicalWorking)}) (subpath {Literal(logicalPersistent)}) (subpath {Literal(canonicalPersistent)}) (subpath {Literal(dotnetRoot)}) (subpath \"/System\") (subpath \"/usr/lib\"))",
            $"(allow file-map-executable (subpath {Literal(logicalContent)}) (subpath {Literal(canonicalContent)}) (subpath {Literal(dotnetRoot)}) (subpath \"/System\") (subpath \"/usr/lib\"))",
            $"(allow file-write* (subpath {Literal(logicalWorking)}) (subpath {Literal(canonicalWorking)}) (subpath {Literal(logicalPersistent)}) (subpath {Literal(canonicalPersistent)}))",
            $"(allow network-outbound (literal {Literal(logicalSocket)}) (literal {Literal(canonicalSocket)}))"
        };
        rules.AddRange(CreateMacNetworkRules(grant.Grant));
        if (HasDeclaredLoopbackListener(manifest))
        {
            rules.Add("(allow network-bind (local ip \"localhost:*\"))");
            rules.Add("(allow network-inbound (local ip \"localhost:*\"))");
        }
        return string.Join(' ', rules);
    }

    private static IEnumerable<string> CreateMacNetworkRules(JsonElement grant)
    {
        if (!grant.TryGetProperty("network", out var network) ||
            !network.TryGetProperty("outbound", out var outbound) ||
            outbound.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var rule in outbound.EnumerateArray())
        {
            var scheme = rule.GetProperty("scheme").GetString();
            var host = rule.GetProperty("host").GetString();
            foreach (var port in rule.GetProperty("ports").EnumerateArray())
            {
                if (scheme == "http" && host is "127.0.0.1" or "::1")
                    yield return $"(allow network-outbound (remote ip \"localhost:{port.GetInt32()}\"))";
                else if (scheme is "https" or "wss" or "grpc+tls")
                    yield return $"(allow network-outbound (remote tcp \"*:{port.GetInt32()}\"))";
            }
        }
    }

    private static string ResolveDotnetRoot()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var sharedDirectory = runtimeDirectory.Parent?.Parent;
        if (sharedDirectory?.Parent is null || !string.Equals(sharedDirectory.Name, "shared", StringComparison.Ordinal))
            throw new ModuleActivationException("dotnet-root-unavailable", "The active .NET installation root could not be resolved.");
        return ResolvePhysicalPath(sharedDirectory.Parent.FullName);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process exited between the state check and termination request.
        }
    }

    private static string ResolvePhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + full : full.StartsWith("/tmp/", StringComparison.Ordinal) ? "/private" + full : full;
    }

    private string CreateSocketPath(InstanceId instance)
    {
        var name = instance.Value[^Math.Min(instance.Value.Length, 40)..] + ".sock";
        var preferred = Path.Combine(_paths.Sockets, name);
        return Encoding.UTF8.GetByteCount(preferred) < 96
            ? preferred
            : Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(), "tmp", "murchalka-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_paths.Root)))[..12] + "-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
    }

    private static string ResolveInside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Artifact entrypoint escapes bundle content.");
        return path;
    }

    private static string SafeInstancePrefix(string moduleId)
    {
        var suffix = moduleId.Split('.').Last();
        return suffix.Length <= 24 ? suffix : suffix[..24];
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken, StringBuilder? tail = null)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (tail is null) continue;
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
    }

    private static async ValueTask DisposeManagedAsync(ManagedModule module)
    {
        if (module.Session is not null) await module.Session.DisposeAsync().ConfigureAwait(false);
        await module.Listener.DisposeAsync().ConfigureAwait(false);
        module.Process.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var instance in _modules.Keys.ToArray()) await StopAsync(instance, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
    }

}
