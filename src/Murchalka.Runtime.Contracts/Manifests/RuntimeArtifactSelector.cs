namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Selects the single compatible process artifact without using declaration order.</summary>
public static class RuntimeArtifactSelector
{
    /// <summary>Selects the process artifact for the current Runtime platform.</summary>
    /// <param name="manifest">The validated manifest.</param>
    /// <param name="protocolVersion">The required Module Protocol major.</param>
    /// <returns>The single compatible process artifact.</returns>
    public static RuntimeArtifact SelectProcess(ModuleManifest manifest, int protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var operatingSystem = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var candidates = manifest.RuntimeArtifacts.Where(value =>
            value.Mode == "process" &&
            value.ProtocolVersion == protocolVersion &&
            (value.OperatingSystems.Count == 0 || value.OperatingSystems.Contains(operatingSystem)) &&
            (value.Architectures.Count == 0 || value.Architectures.Contains(architecture))).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new PlatformNotSupportedException("No compatible process artifact is available."),
            _ => throw new InvalidOperationException("Manifest declares multiple equally compatible process artifacts.")
        };
    }
}
