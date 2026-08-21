namespace Murchalka.Runtime.Contracts.Common;

/// <summary>Provides normalized paths for one Runtime data root.</summary>
public sealed record RuntimePaths
{
    /// <summary>Initializes Runtime paths from an installation data root.</summary>
    /// <param name="root">The Runtime data root.</param>
    public RuntimePaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the normalized Runtime data root.</summary>
    public string Root { get; }
    /// <summary>Gets the configuration directory.</summary>
    public string Configuration => Path.Combine(Root, "configuration");
    /// <summary>Gets the module management root.</summary>
    public string Modules => Path.Combine(Root, "modules");
    /// <summary>Gets the bundle inbox directory.</summary>
    public string Inbox => Path.Combine(Modules, "inbox");
    /// <summary>Gets the staging directory.</summary>
    public string Staging => Path.Combine(Modules, "staging");
    /// <summary>Gets the immutable installed bundle directory.</summary>
    public string Installed => Path.Combine(Modules, "installed");
    /// <summary>Gets the active module marker directory.</summary>
    public string Active => Path.Combine(Modules, "active");
    /// <summary>Gets the disabled module marker directory.</summary>
    public string Disabled => Path.Combine(Modules, "disabled");
    /// <summary>Gets the rejected bundle quarantine directory.</summary>
    public string Quarantine => Path.Combine(Modules, "quarantine");
    /// <summary>Gets the rollback retention directory.</summary>
    public string Rollback => Path.Combine(Modules, "rollback");
    /// <summary>Gets the module cache directory.</summary>
    public string Cache => Path.Combine(Modules, "cache");
    /// <summary>Gets the durable lifecycle state directory.</summary>
    public string State => Path.Combine(Modules, "state");
    /// <summary>Gets the local gateway socket directory.</summary>
    public string Sockets => Path.Combine(Modules, "sockets");
    /// <summary>Gets the module-owned data directory.</summary>
    public string ModuleData => Path.Combine(Root, "module-data");
    /// <summary>Gets the Root audit directory.</summary>
    public string Audit => Path.Combine(Root, "audit");
    /// <summary>Gets the trusted publisher configuration path.</summary>
    public string TrustedPublishers => Path.Combine(Configuration, "trusted-publishers.json");
    /// <summary>Gets the permission grant directory.</summary>
    public string Grants => Path.Combine(Configuration, "grants");
    /// <summary>Gets the administrative binding document path.</summary>
    public string Bindings => Path.Combine(Configuration, "murchalka.bindings.yaml");
    /// <summary>Gets the generated composition lock directory.</summary>
    public string Locks => Path.Combine(Modules, "locks");
    /// <summary>Gets the durable local event fabric root.</summary>
    public string Events => Path.Combine(Root, "events");
    /// <summary>Gets the durable event outbox directory.</summary>
    public string EventOutbox => Path.Combine(Events, "outbox");
    /// <summary>Gets the durable event inbox receipt directory.</summary>
    public string EventInbox => Path.Combine(Events, "inbox");
    /// <summary>Gets the event delivery quarantine directory.</summary>
    public string EventQuarantine => Path.Combine(Events, "quarantine");

    /// <summary>Creates every required Runtime directory when it is absent.</summary>
    public void EnsureCreated()
    {
        foreach (var path in new[] { Configuration, Inbox, Staging, Installed, Active, Disabled, Quarantine, Rollback, Cache, State, Sockets, ModuleData, Audit, Grants, Locks, EventOutbox, EventInbox, EventQuarantine })
            Directory.CreateDirectory(path);
    }
}
