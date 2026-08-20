using System.Text.Json;

namespace Murchalka.Runtime.Contracts.Permissions;

/// <summary>Contains the effective fail-closed permission decision for a bundle.</summary>
/// <param name="Granted">Whether activation is permitted.</param>
/// <param name="ReasonCode">The normalized decision reason.</param>
/// <param name="GrantId">The effective grant identifier.</param>
/// <param name="Revision">The grant revision.</param>
/// <param name="Grant">The effective grant document.</param>
/// <param name="ExpiresAt">The optional expiration time.</param>
public sealed record PermissionDecision(bool Granted, string ReasonCode, string GrantId, long Revision, JsonElement Grant, DateTimeOffset? ExpiresAt);
