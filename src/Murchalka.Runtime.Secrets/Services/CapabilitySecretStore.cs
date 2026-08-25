using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Secrets.Services;

/// <summary>Routes Root Secret Broker persistence through the active secrets provider capability.</summary>
public sealed class CapabilitySecretStore : ISecretStore
{
    private static readonly ModuleId RuntimeModuleId = new("dev.murchalka.runtime");
    private readonly ICapabilityRegistry _capabilities;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    /// <summary>Initializes a provider-backed Root secret store.</summary>
    /// <param name="capabilities">The manifest-authoritative capability registry.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public CapabilitySecretStore(ICapabilityRegistry capabilities, TimeProvider? timeProvider = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SecretMaterial?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateName(name);
        var response = await InvokeAsync(
            JsonSerializer.SerializeToElement(new { operation = "get", name }),
            idempotencyKey: null,
            cancellationToken).ConfigureAwait(false);
        if (!response.GetProperty("found").GetBoolean())
        {
            return null;
        }

        byte[] value;
        try
        {
            value = Convert.FromBase64String(response.GetProperty("value").GetString() ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The secrets provider returned invalid material.", exception);
        }

        return new SecretMaterial(name, response.GetProperty("revision").GetInt64(), value);
    }

    /// <inheritdoc />
    public async Task<SecretVersion> PutAsync(
        string name,
        ReadOnlyMemory<byte> value,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (value.IsEmpty || value.Length > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Secret size must be between 1 byte and 64 KiB.");
        }

        var request = JsonSerializer.SerializeToElement(new
        {
            operation = "put",
            name,
            value = Convert.ToBase64String(value.Span),
            expectedRevision
        });
        var response = await InvokeAsync(request, IdempotencyKey(name, expectedRevision, value.Span), cancellationToken).ConfigureAwait(false);
        return new SecretVersion(
            name,
            response.GetProperty("revision").GetInt64(),
            response.GetProperty("updatedAt").GetDateTimeOffset());
    }

    private async Task<JsonElement> InvokeAsync(JsonElement payload, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var provider = ResolveProvider();
        var now = _timeProvider.GetUtcNow();
        var result = await _capabilities.InvokeAsync(new InvocationEnvelope(
            Guid.NewGuid(),
            provider.CapabilityId,
            provider.Version,
            provider.InstanceId,
            RuntimeModuleId,
            null,
            new InvocationScope(null, null, null, null, null, null),
            "root-secret-broker",
            "root-trust:secret-broker",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            null,
            now.AddSeconds(30),
            idempotencyKey,
            "secrets.provider.request@1",
            payload,
            null), cancellationToken).ConfigureAwait(false);
        if (result.Status != InvocationStatus.Succeeded || result.Payload is null)
        {
            throw new InvalidOperationException($"Secrets provider rejected the operation with code '{result.Error?.Code ?? result.Status.ToString()}'.");
        }

        return result.Payload.Value;
    }

    private CapabilityProvider ResolveProvider()
    {
        var providers = _capabilities.Snapshot()
            .Where(provider =>
                string.Equals(provider.Category, "secrets.provider", StringComparison.Ordinal) &&
                provider.Version.Major == 1)
            .OrderBy(provider => provider.ModuleId.Value, StringComparer.Ordinal)
            .ThenBy(provider => provider.Version)
            .ToArray();
        return providers.Length switch
        {
            1 => providers[0],
            0 => throw new InvalidOperationException("No active secrets.provider@1 capability is available."),
            _ => throw new InvalidOperationException("Multiple secrets.provider@1 capabilities are active; an explicit Root provider binding is required.")
        };
    }

    private static string IdempotencyKey(string name, long expectedRevision, ReadOnlySpan<byte> value)
    {
        var valueDigest = Convert.ToHexStringLower(SHA256.HashData(value));
        var request = Encoding.UTF8.GetBytes(FormattableString.Invariant($"secret-put-v1\n{name}\n{expectedRevision}\n{valueDigest}"));
        return "root-secret-put-" + Convert.ToHexStringLower(SHA256.HashData(request));
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 ||
            !char.IsAsciiLetterOrDigit(name[0]) ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or '-'))
        {
            throw new ArgumentException("Secret name contains unsupported characters.", nameof(name));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
