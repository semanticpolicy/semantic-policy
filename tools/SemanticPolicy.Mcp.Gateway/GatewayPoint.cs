namespace SemanticPolicy.Mcp.Gateway;

/// <summary>One screening point of the gateway file: the policy it evaluates and what it does with each verdict.</summary>
/// <param name="Name"><c>results</c> or <c>definitions</c>, as the gateway file names the point.</param>
/// <param name="Policy">The policy, read and validated.</param>
/// <param name="Mapping">The action for each verdict.</param>
public sealed record GatewayPoint(string Name, Policy Policy, VerdictMapping Mapping);
