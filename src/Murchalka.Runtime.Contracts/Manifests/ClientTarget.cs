namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Identifies a portable Client Runtime host target.</summary>
public enum ClientTarget
{
    /// <summary>A browser shell.</summary>
    Web,
    /// <summary>A cross-platform desktop shell.</summary>
    Desktop,
    /// <summary>A mobile shell.</summary>
    Mobile,
    /// <summary>An extended-reality shell.</summary>
    Xr
}
