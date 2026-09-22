namespace SemanticPolicy.AgentFramework;

/// <summary>
/// How a guard reads one kind of subject: the point it belongs to, the context the layer builds for
/// it when the application supplies none, and where its correlation id is. A guard is constructed
/// with one of the three instances on <see cref="GuardSubject"/>; they are the layer's whole
/// knowledge of its subjects.
/// </summary>
/// <typeparam name="TSubject">What the point judges.</typeparam>
/// <param name="Point">Where in the loop the subject arises.</param>
/// <param name="DefaultContext">The context the layer builds for the subject.</param>
/// <param name="CorrelationId">The subject's id, stamped on a context that came back without one.</param>
internal sealed record GuardSubject<TSubject>(
    InterventionPoint Point,
    Func<TSubject, SemanticContext> DefaultContext,
    Func<TSubject, string> CorrelationId);

/// <summary>The three subjects a guard can read.</summary>
internal static class GuardSubject
{
    /// <summary>The run's input, before the model.</summary>
    public static GuardSubject<ModelInput> PreModel { get; } =
        new(InterventionPoint.PreModel, GuardContext.PreModel, input => input.CorrelationId);

    /// <summary>A tool call, before the tool.</summary>
    public static GuardSubject<ToolCall> PreTool { get; } =
        new(InterventionPoint.PreTool, GuardContext.PreTool, call => call.CorrelationId);

    /// <summary>A tool's result, before the model sees it.</summary>
    public static GuardSubject<ToolResult> PostTool { get; } =
        new(InterventionPoint.PostTool, GuardContext.PostTool, result => result.Call.CorrelationId);
}
