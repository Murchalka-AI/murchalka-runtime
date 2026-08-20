using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Capabilities.Internal;

internal readonly record struct ProviderKey(InstanceId InstanceId, CapabilityId CapabilityId, SemanticVersion Version);
