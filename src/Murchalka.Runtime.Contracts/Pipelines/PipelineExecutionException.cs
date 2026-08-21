namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Represents a normalized dynamic pipeline composition or execution failure.</summary>
public sealed class PipelineExecutionException : Exception
{
    /// <summary>Creates a pipeline failure.</summary>
    /// <param name="reasonCode">The stable failure reason.</param>
    /// <param name="message">The diagnostic message.</param>
    public PipelineExecutionException(string reasonCode, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the stable failure reason.</summary>
    public string ReasonCode { get; }
}
