namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// The action a point takes on each verdict but Allow, which always passes. Every text the model sees is the
/// operator's; the gateway has no default of its own.
/// </summary>
/// <param name="Warn">On <see cref="Verdict.Warn"/>.</param>
/// <param name="Escalate">On <see cref="Verdict.Escalate"/>.</param>
/// <param name="Deny">On <see cref="Verdict.Deny"/>.</param>
/// <param name="Abstain">On <see cref="Verdict.Abstain"/>.</param>
public sealed record VerdictMapping(MappedAction Warn, MappedAction Escalate, MappedAction Deny, MappedAction Abstain);

/// <summary>An action and the operator's text that goes with it.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Message">
/// The text the model is shown, or <see langword="null"/> for <see cref="GatewayAction.Pass"/>, which shows nothing. For
/// <see cref="GatewayAction.Ask"/>, the text the person is shown in the host's dialog.
/// </param>
public sealed record MappedAction(GatewayAction Action, string? Message)
{
    /// <summary>
    /// For <see cref="GatewayAction.Ask"/>, the text the model is shown, as an error result, when the person does not
    /// accept. <see langword="null"/> for every other action.
    /// </summary>
    public string? Withheld { get; init; }

    /// <summary>
    /// For <see cref="GatewayAction.Ask"/>, the action taken instead when the host cannot show a form; never itself an
    /// ask. <see langword="null"/> for every other action.
    /// </summary>
    public MappedAction? Fallback { get; init; }
}
