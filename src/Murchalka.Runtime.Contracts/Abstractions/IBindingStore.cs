using System.Text.Json;
using Murchalka.Runtime.Contracts.Bindings;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Reads and atomically replaces revisioned administrative bindings.</summary>
public interface IBindingStore
{
    /// <summary>Gets the current validated binding document.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current binding document.</returns>
    Task<BindingDocument> GetAsync(CancellationToken cancellationToken);

    /// <summary>Validates and atomically stores a new binding document.</summary>
    /// <param name="document">The untrusted binding document.</param>
    /// <param name="expectedRevision">The revision that the caller observed.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The validated stored document.</returns>
    Task<BindingDocument> ReplaceAsync(JsonElement document, long expectedRevision, CancellationToken cancellationToken);
}
