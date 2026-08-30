using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Permissions;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record EventSubscriptionRegistration(
    ModuleId ModuleId,
    InstanceId InstanceId,
    string Topic,
    string HandlerId,
    string SchemaDigest,
    PermissionDecision Permission);
