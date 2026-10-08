namespace SemanticPolicy.Mcp.Gateway;

/// <summary>What the gateway does with what a point screened, as the gateway file names it.</summary>
public enum GatewayAction
{
    /// <summary>Lets it through unchanged.</summary>
    Pass,

    /// <summary>Puts the operator's message, as text, before a tool result's content.</summary>
    Annotate,

    /// <summary>Replaces a tool result's content with the operator's message, and marks the result as an error.</summary>
    Withhold,

    /// <summary>Takes a tool off the list; a call to it is answered with the operator's message.</summary>
    Hide,
}
