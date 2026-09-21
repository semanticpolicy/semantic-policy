namespace SemanticPolicy.Protocol;

/// <summary>
/// The protocol versions this library speaks. A change to the wire shape is a new version, never an
/// edit of an existing one.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>The frozen v0 shape: one context, one typed question, an answer with typed evidence.</summary>
    public const string V0 = "semanticpolicy/v0";
}
