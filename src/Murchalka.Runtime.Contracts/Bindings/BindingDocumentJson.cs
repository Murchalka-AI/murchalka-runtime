using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Serializes validated binding domain models to the canonical version-one API shape.</summary>
public static class BindingDocumentJson
{
    /// <summary>Serializes a binding document for the administrative API.</summary>
    /// <param name="document">The validated binding document.</param>
    /// <returns>The canonical JSON representation.</returns>
    public static JsonElement Serialize(BindingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new JsonObject
        {
            ["apiVersion"] = "bindings.murchalka.dev/v1",
            ["kind"] = "ModuleBindings",
            ["metadata"] = new JsonObject { ["installation"] = document.Installation, ["revision"] = document.Revision },
            ["bindings"] = new JsonArray(document.Bindings.Select(value => (JsonNode)new JsonObject
            {
                ["id"] = value.Id,
                ["consumer"] = new JsonObject { ["module"] = value.ConsumerModule.Value, ["requirement"] = value.RequirementId },
                ["scope"] = Scope(value.Scope),
                ["provider"] = Provider(value.Provider)
            }).ToArray()),
            ["policies"] = new JsonObject
            {
                ["ambiguity"] = "fail",
                ["missingScopedBinding"] = document.Policies.InheritParentScopes ? "inheritParent" : "fail",
                ["providerUnavailable"] = document.Policies.AllowDeclaredFailover ? "failoverOnlyIfDeclared" : "fail"
            }
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject Scope(BindingScope scope)
    {
        var result = new JsonObject { ["type"] = Name(scope.Type) };
        if (scope.Id is not null) result["id"] = scope.Id;
        return result;
    }

    private static JsonObject Provider(ProviderSelection selection)
    {
        if (selection.Failover.Count == 0) return ProviderReference(selection.Primary);
        return new JsonObject
        {
            ["primary"] = ProviderReference(selection.Primary),
            ["failover"] = new JsonArray(selection.Failover.Select(value => (JsonNode)ProviderReference(value)).ToArray()),
            ["failoverPolicy"] = new JsonObject
            {
                ["allowedDataClasses"] = new JsonArray(selection.AllowedDataClasses.Order(StringComparer.Ordinal).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                ["maxAttempts"] = selection.MaximumAttempts
            }
        };
    }

    private static JsonObject ProviderReference(ProviderReference value) => new()
    {
        ["module"] = value.ModuleId.Value,
        ["capability"] = value.CapabilityId.Value,
        ["instance"] = value.Instance
    };

    private static string Name(BindingScopeType value) => value switch
    {
        BindingScopeType.Global => "global",
        BindingScopeType.Tenant => "tenant",
        BindingScopeType.Workspace => "workspace",
        BindingScopeType.Person => "person",
        BindingScopeType.Group => "group",
        BindingScopeType.Module => "module",
        BindingScopeType.Node => "node",
        BindingScopeType.Session => "session",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
