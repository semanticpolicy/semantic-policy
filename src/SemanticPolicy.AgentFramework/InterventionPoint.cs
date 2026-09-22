namespace SemanticPolicy.AgentFramework;

/// <summary>
/// Where in an agent's loop a guard sits, which fixes what it judges and what its outcome can do.
/// </summary>
internal enum InterventionPoint
{
    /// <summary>Before the model runs, on the run's input.</summary>
    PreModel,

    /// <summary>Before a tool the model proposed runs, on the call.</summary>
    PreTool,

    /// <summary>After a tool ran and before the model sees what it returned, on the result.</summary>
    PostTool,
}
