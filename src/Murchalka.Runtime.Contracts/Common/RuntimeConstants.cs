using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Common;

/// <summary>Defines the supported Runtime version and security-related limits.</summary>
public static class RuntimeConstants
{
    /// <summary>Gets the current Runtime semantic version.</summary>
    public static SemanticVersion Version { get; } = new(0, 2, 5);
    /// <summary>The supported Module Protocol major version.</summary>
    public const int ProtocolVersion = 1;
    /// <summary>The maximum compressed module bundle size.</summary>
    public const long MaximumBundleBytes = 256L * 1024 * 1024;
    /// <summary>The maximum total expanded bundle size.</summary>
    public const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    /// <summary>The maximum number of archive entries.</summary>
    public const int MaximumArchiveEntries = 20_000;
    /// <summary>The maximum length-prefixed Module Protocol frame size.</summary>
    public const int MaximumProtocolFrameBytes = 16 * 1024 * 1024;
}
