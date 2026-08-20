using Murchalka.Runtime.Contracts.Capabilities;

namespace Murchalka.Runtime.Capabilities.Internal;

internal sealed record RegisteredProvider(CapabilityProvider Provider, ContractPolicy Policy);
