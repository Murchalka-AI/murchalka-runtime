namespace Murchalka.Runtime.Secrets.Internal;

internal sealed record StoredSecret(string Name, long Revision, byte[] Nonce, byte[] Ciphertext, byte[] Tag, DateTimeOffset UpdatedAt);
