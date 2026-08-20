using System.Text.Json.Nodes;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Bindings.Internal;

internal static class BindingDocumentParser
{
    public static BindingDocument Parse(JsonNode document)
    {
        var root = document.AsObject();
        var metadata = root["metadata"]!.AsObject();
        var bindings = root["bindings"]!.AsArray().Select(ParseBinding).ToArray();
        RejectDuplicateIds(bindings);
        RejectDuplicateTargets(bindings);
        var policies = root["policies"]!.AsObject();
        return new BindingDocument(
            metadata["installation"]!.GetValue<string>(),
            metadata["revision"]!.GetValue<long>(),
            bindings,
            new BindingPolicies(
                policies["missingScopedBinding"]!.GetValue<string>() == "inheritParent",
                policies["providerUnavailable"]!.GetValue<string>() == "failoverOnlyIfDeclared"));
    }

    private static ModuleBinding ParseBinding(JsonNode? node)
    {
        var value = node!.AsObject();
        var consumer = value["consumer"]!.AsObject();
        var scope = value["scope"]!.AsObject();
        return new ModuleBinding(
            value["id"]!.GetValue<string>(),
            new ModuleId(consumer["module"]!.GetValue<string>()),
            consumer["requirement"]!.GetValue<string>(),
            new BindingScope(ParseScope(scope["type"]!.GetValue<string>()), scope["id"]?.GetValue<string>()),
            ParseSelection(value["provider"]!.AsObject()));
    }

    private static ProviderSelection ParseSelection(JsonObject value)
    {
        if (value["primary"] is not JsonObject primary)
            return new ProviderSelection(ParseProvider(value), [], new HashSet<string>(StringComparer.Ordinal), 0);
        var policy = value["failoverPolicy"]!.AsObject();
        return new ProviderSelection(
            ParseProvider(primary),
            value["failover"]!.AsArray().Select(item => ParseProvider(item!.AsObject())).ToArray(),
            policy["allowedDataClasses"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
            policy["maxAttempts"]!.GetValue<int>());
    }

    private static ProviderReference ParseProvider(JsonObject value) => new(
        new ModuleId(value["module"]!.GetValue<string>()),
        new CapabilityId(value["capability"]!.GetValue<string>()),
        value["instance"]!.GetValue<string>());

    private static BindingScopeType ParseScope(string value) => value switch
    {
        "global" => BindingScopeType.Global,
        "tenant" => BindingScopeType.Tenant,
        "workspace" => BindingScopeType.Workspace,
        "person" => BindingScopeType.Person,
        "group" => BindingScopeType.Group,
        "module" => BindingScopeType.Module,
        "node" => BindingScopeType.Node,
        "session" => BindingScopeType.Session,
        _ => throw new InvalidDataException($"Unknown binding scope '{value}'.")
    };

    private static void RejectDuplicateIds(IReadOnlyList<ModuleBinding> bindings)
    {
        var duplicate = bindings.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Binding id '{duplicate.Key}' is duplicated.");
    }

    private static void RejectDuplicateTargets(IReadOnlyList<ModuleBinding> bindings)
    {
        var duplicate = bindings.GroupBy(value => (value.ConsumerModule, value.RequirementId, value.Scope)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Multiple bindings target '{duplicate.Key.ConsumerModule.Value}/{duplicate.Key.RequirementId}' at the same scope.");
    }
}
