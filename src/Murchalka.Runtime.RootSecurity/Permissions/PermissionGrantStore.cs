using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.RootSecurity.Json;
using Murchalka.Runtime.RootSecurity.Trust;

namespace Murchalka.Runtime.RootSecurity.Permissions;

/// <summary>Validates signed permission grants against a verified module bundle.</summary>
public sealed class PermissionGrantStore : IPermissionGrantStore
{
    private readonly RuntimePaths _paths;
    private readonly TrustedKeyStore _trust;
    private readonly CanonicalSchemaValidator _schemas = CanonicalSchemaValidator.CreateBundled();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a permission grant store.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    /// <param name="trust">The trusted key store.</param>
    /// <param name="timeProvider">The optional source of current time.</param>
    public PermissionGrantStore(RuntimePaths paths, TrustedKeyStore trust, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<PermissionDecision> EvaluateAsync(VerifiedBundle bundle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = JsonNode.Parse(bundle.Manifest.RequestedPermissions.GetRawText()) ?? new JsonObject();
        if (IsEffectivelyEmpty(requested))
            return Task.FromResult(new PermissionDecision(true, "implicit-empty-grant", "implicit-empty", 0, JsonSerializer.SerializeToElement(new JsonObject()), null));

        var jsonPath = Path.Combine(_paths.Grants, bundle.Manifest.Id.Value + ".json");
        var yamlPath = Path.Combine(_paths.Grants, bundle.Manifest.Id.Value + ".yaml");
        var path = File.Exists(jsonPath) ? jsonPath : File.Exists(yamlPath) ? yamlPath : null;
        if (path is null) return Task.FromResult(Denied("grant-missing"));
        JsonNode document;
        try { document = StructuredDocument.Load(path); }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        { return Task.FromResult(Denied("grant-invalid:" + exception.GetType().Name)); }
        var report = _schemas.ValidateJson("permission-grant.schema.json", document);
        if (!report.IsValid) return Task.FromResult(Denied("grant-schema-invalid"));
        var root = document.AsObject();
        var metadata = root["metadata"]!.AsObject();
        var grant = root["grant"]!;
        var signature = root["signature"]!.AsObject();
        if (metadata["module"]!.GetValue<string>() != bundle.Manifest.Id.Value ||
            !VersionRangeExpression.Parse(metadata["moduleVersionRange"]!.GetValue<string>()).Satisfies(bundle.Manifest.Version) ||
            metadata["bundleDigest"]!.GetValue<string>() != bundle.Identity.Digest)
            return Task.FromResult(Denied("grant-identity-mismatch"));
        var issuedAt = DateTimeOffset.Parse(metadata["issuedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        DateTimeOffset? expiresAt = metadata["expiresAt"] is null ? null : DateTimeOffset.Parse(metadata["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var now = _timeProvider.GetUtcNow();
        if (issuedAt > now.AddMinutes(1) || expiresAt is not null && expiresAt <= now) return Task.FromResult(Denied("grant-expired-or-not-yet-valid"));
        if (!VerifySignature(root, signature)) return Task.FromResult(Denied("grant-signature-invalid"));
        if (!Contains(grant, requested)) return Task.FromResult(Denied("grant-does-not-cover-request"));
        var grantId = metadata["grantId"]!.GetValue<string>();
        return Task.FromResult(new PermissionDecision(true, "grant-valid", grantId, File.GetLastWriteTimeUtc(path).Ticks, JsonSerializer.SerializeToElement(grant), expiresAt));
    }

    private bool VerifySignature(JsonObject root, JsonObject signature)
    {
        var keyId = signature["keyId"]!.GetValue<string>();
        var trusted = _trust.FindGrantAuthority(keyId);
        if (trusted is null) return false;
        byte[] value;
        try { value = Convert.FromBase64String(signature["value"]!.GetValue<string>()); }
        catch (FormatException) { return false; }
        var clone = root.DeepClone().AsObject();
        clone.Remove("signature");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(trusted.PublicKeyPem);
        return ecdsa.VerifyData(JsonCanonicalizer.Serialize(clone), value, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool Contains(JsonNode? granted, JsonNode? requested)
    {
        if (IsEffectivelyEmpty(requested)) return true;
        if (granted is null || requested is null) return false;
        if (requested is JsonObject requestedObject)
        {
            if (granted is not JsonObject grantedObject) return false;
            return requestedObject.All(pair => IsEffectivelyEmpty(pair.Value) || grantedObject.TryGetPropertyValue(pair.Key, out var child) && Contains(child, pair.Value));
        }
        if (requested is JsonArray requestedArray)
        {
            if (granted is not JsonArray grantedArray) return false;
            var grantedValues = grantedArray.Select(item => JsonCanonicalizer.Text(item!)).ToHashSet(StringComparer.Ordinal);
            return requestedArray.All(item => item is not null && grantedValues.Contains(JsonCanonicalizer.Text(item)));
        }
        return JsonNode.DeepEquals(granted, requested);
    }

    private static bool IsEffectivelyEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonObject value => value.All(pair => IsEffectivelyEmpty(pair.Value)),
        JsonArray value => value.Count == 0,
        JsonValue value when value.TryGetValue<bool>(out var boolean) => !boolean,
        _ => false
    };

    private static PermissionDecision Denied(string reason) => new(false, reason, string.Empty, 0, JsonSerializer.SerializeToElement(new JsonObject()), null);
}
