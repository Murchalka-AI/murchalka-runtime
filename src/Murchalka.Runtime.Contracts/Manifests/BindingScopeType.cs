namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Identifies a supported administrative binding scope.</summary>
public enum BindingScopeType
{
    /// <summary>The installation-wide scope.</summary>
    Global,
    /// <summary>A tenant scope.</summary>
    Tenant,
    /// <summary>A workspace scope.</summary>
    Workspace,
    /// <summary>A person scope.</summary>
    Person,
    /// <summary>A group scope.</summary>
    Group,
    /// <summary>A consuming-module scope.</summary>
    Module,
    /// <summary>A node scope.</summary>
    Node,
    /// <summary>A session scope.</summary>
    Session
}
