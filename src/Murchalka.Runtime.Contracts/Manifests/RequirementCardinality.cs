namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Defines how many providers may satisfy a capability requirement.</summary>
public enum RequirementCardinality
{
    /// <summary>Exactly one provider is required.</summary>
    ExactlyOne,
    /// <summary>Zero or one provider may be selected.</summary>
    ZeroOrOne,
    /// <summary>At least one provider is required and fan-out is allowed.</summary>
    OneOrMany,
    /// <summary>Any number of providers may be selected.</summary>
    ZeroOrMany,
    /// <summary>Every matching provider is selected.</summary>
    AllMatching
}
