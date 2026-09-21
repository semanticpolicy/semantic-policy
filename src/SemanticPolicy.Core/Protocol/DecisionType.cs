namespace SemanticPolicy.Protocol;

/// <summary>The shape of the answer a request asks for.</summary>
public enum DecisionType
{
    /// <summary>A yes-or-no answer, with evidence keyed by <c>true</c> and <c>false</c>.</summary>
    Boolean,

    /// <summary>One of the request's named options, with evidence keyed by option.</summary>
    Choice,

    /// <summary>One of the request's ordered levels, with evidence keyed by level.</summary>
    Score,
}
