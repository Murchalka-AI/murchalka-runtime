using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    public async Task<IModuleGatewaySession> StartAsync(InstalledBundle bundle, PermissionDecision grant, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        var artifact = SelectArtifact(bundle.Manifest);
        var artifactPath = ResolveInside(bundle.ContentPath, artifact.EntryPoint);
        if (!File.Exists(artifactPath)) throw new FileNotFoundException("Selected module artifact is missing.", artifactPath);
        var instance = new InstanceId($"{SafeInstancePrefix(bundle.Manifest.Id.Value)}-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(_paths.ModuleData, bundle.Manifest.Id.Value, "instances", instance.Value);
        Directory.CreateDirectory(workingDirectory);
        var socketPath = CreateSocketPath(instance);
        var listener = new ModuleGatewayListener(socketPath);
        var proofKey = RandomNumberGenerator.GetBytes(32);
        var startInfo = CreateStartInfo(artifactPath, workingDirectory, socketPath, bundle, artifact, instance, proofKey);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) { await listener.DisposeAsync().ConfigureAwait(false); throw new InvalidOperationException("Module process could not be started."); }
        var managed = new ManagedModule(bundle.Manifest.Id, instance, process, listener);
        if (!_modules.TryAdd(instance, managed)) { process.Kill(entireProcessTree: true); await listener.DisposeAsync().ConfigureAwait(false); throw new InvalidOperationException("Module instance id collision."); }
        managed.OutputDrain = DrainAsync(process.StandardOutput, CancellationToken.None);
        managed.ErrorDrain = DrainAsync(process.StandardError, CancellationToken.None, managed.ErrorTail);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(bundle.Manifest.Health.StartupTimeout);
            var accept = listener.AcceptAsync(bundle, artifact, instance, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), proofKey, grant, startup.Token);
            var exited = process.WaitForExitAsync(startup.Token);
            var completed = await Task.WhenAny(accept, exited).ConfigureAwait(false);
            if (completed == exited)
            {
                if (managed.ErrorDrain is not null) await managed.ErrorDrain.ConfigureAwait(false);
                throw new InvalidOperationException($"Module process exited with code {process.ExitCode} before protocol readiness: {managed.ErrorTail}.");
            }
            var session = await accept.ConfigureAwait(false);
            managed.Session = session;
            _ = MonitorExitAsync(managed);
            return session;
        }
        catch
        {
            managed.Stopping = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException)
        {
            if (!module.Process.HasExited) module.Process.Kill(entireProcessTree: true);
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

    private static RuntimeArtifact SelectArtifact(ModuleManifest manifest)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var candidates = manifest.RuntimeArtifacts.Where(value => value.Mode == "process" && value.ProtocolVersion == RuntimeConstants.ProtocolVersion &&
            (value.OperatingSystems.Count == 0 || value.OperatingSystems.Contains(os)) && (value.Architectures.Count == 0 || value.Architectures.Contains(architecture))).ToArray();
        return candidates.Length == 1 ? candidates[0] : candidates.Length == 0
            ? throw new PlatformNotSupportedException("No compatible process artifact is available.")
            : throw new InvalidOperationException("Manifest declares multiple equally compatible process artifacts.");
    }

    private static ProcessStartInfo CreateStartInfo(string artifactPath, string workingDirectory, string socketPath, InstalledBundle bundle, RuntimeArtifact artifact, InstanceId instance, byte[] proofKey)
    {
        var dotnetRoot = ResolveDotnetRoot();
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsMacOS() ? "/usr/bin/sandbox-exec" : artifactPath,
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
            start.ArgumentList.Add(CreateMacSandboxProfile(artifactPath, bundle.ContentPath, workingDirectory, socketPath, dotnetRoot));
            start.ArgumentList.Add(artifactPath);
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
        start.Environment["MURCHALKA_MODULE_DATA"] = workingDirectory;
        return start;
    }

    private static string CreateMacSandboxProfile(string artifactPath, string contentPath, string workingDirectory, string socketPath, string dotnetRoot)
    {
        static string Literal(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var logicalArtifact = Path.GetFullPath(artifactPath);
        var logicalContent = Path.GetFullPath(contentPath);
        var logicalWorking = Path.GetFullPath(workingDirectory);
        var logicalSocket = Path.GetFullPath(socketPath);
        var canonicalContent = ResolvePhysicalPath(contentPath);
        var canonicalWorking = ResolvePhysicalPath(workingDirectory);
        var canonicalSocket = ResolvePhysicalPath(socketPath);
        return string.Join(' ',
            "(version 1)",
            "(deny default)",
            "(import \"system.sb\")",
            "(deny network*)",
            "(deny process-fork)",
            $"(allow process-exec (literal {Literal(logicalArtifact)}) (literal {Literal(ResolvePhysicalPath(artifactPath))}))",
            "(allow process-info*)",
            "(allow file-read-metadata)",
            $"(allow file-read* (subpath {Literal(logicalContent)}) (subpath {Literal(canonicalContent)}) (subpath {Literal(dotnetRoot)}) (subpath \"/System\") (subpath \"/usr/lib\"))",
            $"(allow file-write* (subpath {Literal(logicalWorking)}) (subpath {Literal(canonicalWorking)}))",
            $"(allow network-outbound (literal {Literal(logicalSocket)}) (literal {Literal(canonicalSocket)}))");
    }

    private static string ResolveDotnetRoot()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var sharedDirectory = runtimeDirectory.Parent?.Parent;
        if (sharedDirectory?.Parent is null || !string.Equals(sharedDirectory.Name, "shared", StringComparison.Ordinal))
            throw new InvalidOperationException("The active .NET installation root could not be resolved.");
        return ResolvePhysicalPath(sharedDirectory.Parent.FullName);
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
