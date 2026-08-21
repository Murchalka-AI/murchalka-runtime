namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Defines how a pipeline continues after a handler failure.</summary>
public enum PipelineFailureMode
{
    /// <summary>Stops pipeline execution and returns the failure.</summary>
    Fail,
    /// <summary>Preserves the current accumulator and continues.</summary>
    Continue,
    /// <summary>Allows the next ordered handler to provide a fallback result.</summary>
    Fallback
}
