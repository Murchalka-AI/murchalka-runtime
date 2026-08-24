using System.Security.Cryptography;
using System.Text.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Secrets.Services;

/// <summary>Enforces manifest and grant boundaries before issuing audited secret leases.</summary>
public sealed class RootSecretBroker : ISecretBroker
{
    private readonly ISecretStore _store;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the non-bypassable Root secret broker.</summary>
    /// <param name="store">The encrypted secret store.</param>
    /// <param name="audit">The Root audit trail.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public RootSecretBroker(ISecretStore store, IRootAudit audit, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SecretLease> LeaseAsync(InstalledBundle bundle, PermissionDecision grant, SecretLeaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        var now = _timeProvider.GetUtcNow();
        if (request.Deadline <= now) throw new TimeoutException("Secret lease request deadline has elapsed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        if (!grant.Granted || grant.ExpiresAt is not null && grant.ExpiresAt <= now) throw new UnauthorizedAccessException("The effective permission grant is unavailable or expired.");
        if (!ContainsSecret(bundle.Manifest.RequestedPermissions, request.Name) || !ContainsSecret(grant.Grant, request.Name))
            throw new UnauthorizedAccessException("The secret is not covered by both the manifest request and effective grant.");

        var material = await _store.GetAsync(request.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The requested secret is not configured.");
        try
        {
            var expiresAt = now.AddMinutes(5);
            if (request.Deadline < expiresAt) expiresAt = request.Deadline;
            if (grant.ExpiresAt is { } grantExpiry && grantExpiry < expiresAt) expiresAt = grantExpiry;
            var lease = new SecretLease(request.OperationId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), request.Name,
                material.Revision, Convert.ToBase64String(material.Value), now, expiresAt);
            await _audit.AppendAsync("secret.leased", bundle.Manifest.Id.Value, "success", "bounded-secret-lease-issued", new Dictionary<string, string?>
            {
                ["secret"] = request.Name,
                ["secretRevision"] = material.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["leaseId"] = lease.LeaseId,
                ["purpose"] = request.Purpose,
                ["expiresAt"] = lease.ExpiresAt.ToString("O")
            }, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        finally { CryptographicOperations.ZeroMemory(material.Value); }
    }

    private static bool ContainsSecret(JsonElement document, string name)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("secrets", out var secrets) || secrets.ValueKind != JsonValueKind.Array) return false;
        return secrets.EnumerateArray().Any(value => string.Equals(value.GetString(), name, StringComparison.Ordinal));
    }
}
