using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Kernel.Services;

namespace Murchalka.Runtime.Host.Bootstrap;

internal static class DeploymentBootstrapper
{
    public static async Task ApplyAsync(
        RuntimeKernel kernel,
        string? bindingsPath,
        string? configurationDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (bindingsPath is not null)
            await ApplyBindingsAsync(kernel, bindingsPath, cancellationToken).ConfigureAwait(false);
        if (configurationDirectory is not null)
            await ApplyConfigurationsAsync(kernel, configurationDirectory, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyBindingsAsync(RuntimeKernel kernel, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bootstrap bindings document was not found.", path);
        var node = StructuredDocument.Load(path);
        var document = JsonSerializer.SerializeToElement(node);
        var revision = document.GetProperty("metadata").GetProperty("revision").GetInt64();
        var current = await kernel.GetBindingsAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision == revision) return;
        if (revision != checked(current.Revision + 1))
            throw new InvalidDataException($"Bootstrap binding revision must advance from {current.Revision} to {checked(current.Revision + 1)}.");
        await kernel.ReplaceBindingsAsync(document, current.Revision, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyConfigurationsAsync(
        RuntimeKernel kernel,
        string directory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Bootstrap configuration directory was not found.");
        var files = Directory.GetFiles(directory, "dev.murchalka.*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("Bootstrap configuration directory contains no module snapshots.");
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        foreach (var file in files)
        {
            var moduleId = new ModuleId(Path.GetFileNameWithoutExtension(file));
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
            var revision = document.RootElement.GetProperty("revision").GetInt64();
            var values = document.RootElement.GetProperty("values").Clone();
            var current = await WaitForConfigurationAsync(kernel, moduleId, deadline, cancellationToken).ConfigureAwait(false);
            if (current.Revision == revision) continue;
            if (revision != checked(current.Revision + 1))
                throw new InvalidDataException($"Bootstrap configuration for '{moduleId}' must advance from {current.Revision} to {checked(current.Revision + 1)}.");
            var updated = await kernel.ReplaceConfigurationAsync(moduleId, values, current.Revision, cancellationToken).ConfigureAwait(false);
            if (updated is null) throw new InvalidOperationException($"Module '{moduleId}' disappeared during bootstrap configuration.");
        }
    }

    private static async Task<Murchalka.Runtime.Contracts.Configuration.ModuleConfigurationSnapshot> WaitForConfigurationAsync(
        RuntimeKernel kernel,
        ModuleId moduleId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await kernel.GetConfigurationAsync(moduleId, cancellationToken).ConfigureAwait(false) is { } snapshot)
                return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Module '{moduleId}' was not installed before the bootstrap timeout elapsed.");
    }
}
