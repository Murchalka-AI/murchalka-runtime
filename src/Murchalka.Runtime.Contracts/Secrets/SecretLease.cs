namespace Murchalka.Runtime.Contracts.Secrets;

/// <summary>Contains short-lived secret material issued to one authenticated module instance.</summary>
/// <param name="OperationId">The matching request identifier.</param>
/// <param name="LeaseId">The non-reusable lease identifier.</param>
/// <param name="Name">The secret name.</param>
/// <param name="Revision">The secret revision.</param>
/// <param name="Value">The Base64-encoded secret bytes.</param>
/// <param name="IssuedAt">The lease issue time.</param>
/// <param name="ExpiresAt">The bounded lease expiration time.</param>
public sealed record SecretLease(
    string OperationId,
    string LeaseId,
    string Name,
    long Revision,
    string Value,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
