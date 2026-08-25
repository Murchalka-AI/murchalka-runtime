using System.Security.Cryptography;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Tests.Security;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, SecretMaterial> _secrets = new(StringComparer.Ordinal);

    public Task<SecretMaterial?> GetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_secrets.TryGetValue(name, out var material)
            ? new SecretMaterial(material.Name, material.Revision, material.Value.ToArray())
            : null);
    }

    public Task<SecretVersion> PutAsync(string name, ReadOnlyMemory<byte> value, long expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actual = _secrets.TryGetValue(name, out var current) ? current.Revision : 0;
        if (actual != expectedRevision)
        {
            throw new InvalidOperationException("Secret revision conflict.");
        }

        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current.Value);
        }

        var material = new SecretMaterial(name, checked(actual + 1), value.ToArray());
        _secrets[name] = material;
        return Task.FromResult(new SecretVersion(name, material.Revision, DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        foreach (var material in _secrets.Values)
        {
            CryptographicOperations.ZeroMemory(material.Value);
        }

        _secrets.Clear();
    }
}
