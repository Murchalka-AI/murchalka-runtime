namespace Murchalka.Runtime.Contracts.Secrets;

/// <summary>Contains one decrypted secret revision for immediate broker use.</summary>
/// <param name="Name">The stable secret name.</param>
/// <param name="Revision">The monotonic secret revision.</param>
/// <param name="Value">The secret bytes. Callers must clear the buffer after use.</param>
public sealed record SecretMaterial(string Name, long Revision, byte[] Value);
