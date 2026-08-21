namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Defines the execution semantics of a pipeline stage.</summary>
public enum PipelineStageMode
{
    /// <summary>Runs handlers in dependency order and passes each result to the next handler.</summary>
    Sequential,
    /// <summary>Runs handlers concurrently and merges their object results.</summary>
    ParallelMerge,
    /// <summary>Uses the first handler that completes successfully.</summary>
    FirstSuccessful,
    /// <summary>Requires one administratively selected handler.</summary>
    ExactlyOne,
    /// <summary>Runs every handler and returns their results as an array.</summary>
    FanOut,
    /// <summary>Folds handler results over the current accumulator in dependency order.</summary>
    Reduce
}
