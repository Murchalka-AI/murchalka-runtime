namespace Murchalka.Runtime.Contracts.Secrets;

/// <summary>Requests a bounded secret lease from an authenticated module session.</summary>
/// <param name="OperationId">The request identifier.</param>
/// <param name="Name">The manifest-declared secret name.</param>
/// <param name="Purpose">The non-empty purpose for secret use.</param>
/// <param name="Deadline">The latest acceptable response time.</param>
public sealed record SecretLeaseRequest(string OperationId, string Name, string Purpose, DateTimeOffset Deadline);
