using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Bindings.Internal;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Common;

namespace Murchalka.Runtime.Bindings.Services;

/// <summary>Stores one schema-validated binding document with optimistic concurrency.</summary>
public sealed class FileBindingStore : IBindingStore, IDisposable
{
    private readonly RuntimePaths _paths;
    private readonly string _installation;
    private readonly CanonicalSchemaValidator _schemas = CanonicalSchemaValidator.CreateBundled();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Initializes a binding store.</summary>
    /// <param name="paths">The Runtime paths.</param>
    /// <param name="installation">The local installation identifier.</param>
    public FileBindingStore(RuntimePaths paths, string installation)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentException.ThrowIfNullOrWhiteSpace(installation);
        _installation = installation;
        _paths.EnsureCreated();
    }

    /// <inheritdoc/>
    public async Task<BindingDocument> GetAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Read(); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<BindingDocument> ReplaceAsync(JsonElement document, long expectedRevision, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Read();
            if (current.Revision != expectedRevision)
                throw new BindingRevisionConflictException(expectedRevision, current.Revision);
            var node = JsonNode.Parse(document.GetRawText()) ?? throw new InvalidDataException("Binding document is empty.");
            var updated = ValidateAndParse(node);
            if (!string.Equals(updated.Installation, _installation, StringComparison.Ordinal))
                throw new InvalidDataException($"Binding installation '{updated.Installation}' does not match '{_installation}'.");
            if (updated.Revision != checked(current.Revision + 1))
                throw new InvalidDataException($"Binding revision must advance from {current.Revision} to {checked(current.Revision + 1)}.");
            var temporary = _paths.Bindings + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _paths.Bindings, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return updated;
        }
        finally { _gate.Release(); }
    }

    private BindingDocument Read()
    {
        if (!File.Exists(_paths.Bindings)) return BindingDocument.Empty(_installation);
        return ValidateAndParse(StructuredDocument.Load(_paths.Bindings));
    }

    private BindingDocument ValidateAndParse(JsonNode node)
    {
        var report = _schemas.ValidateJson("binding.schema.json", node);
        if (!report.IsValid)
            throw new InvalidDataException("Binding schema validation failed: " + string.Join("; ", report.Violations.Select(value => $"{value.InstanceLocation}:{value.Message}")));
        return BindingDocumentParser.Parse(node);
    }

    /// <summary>Releases synchronization resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
