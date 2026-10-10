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

    /// <summary>
    /// Asks the person, through the host, whether a tool result may pass: it passes unchanged only when they accept, and
    /// is withheld otherwise. A host that cannot ask gets the entry's fallback action instead.
    /// </summary>
    Ask,
}
