using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Permissions;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record EventPublicationRegistration(
    ModuleId ModuleId,
    InstanceId InstanceId,
    string Topic,
    string SchemaDigest,
    JsonSchema Schema,
    PermissionDecision Permission);
