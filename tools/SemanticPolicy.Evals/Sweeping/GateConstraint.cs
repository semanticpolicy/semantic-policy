namespace SemanticPolicy.Evals.Sweeping;

/// <summary>Which number of the gate curve a gate constraint bounds.</summary>
public enum GateConstraintKind
{
    /// <summary><c>max-abstain=x</c>: an abstention rate of at most x; the highest gate that keeps it is recommended.</summary>
    MaxAbstain,

    /// <summary><c>min-accuracy=y</c>: accuracy on decided rows of at least y; the lowest gate that keeps it.</summary>
    MinAccuracy,
}

/// <summary>
/// A bound on a binding's margin gate, as given to <c>--gate</c>. A gate trades abstentions for accuracy on the
/// rows it lets through, so the two bounds pull in opposite directions and each names its own end.
/// </summary>
/// <param name="Kind">The number that is bounded.</param>
/// <param name="Value">The bound, in [0, 1].</param>
public sealed record GateConstraint(GateConstraintKind Kind, double Value)
{
    private const string _option = "--gate";
    private const string _expected = "max-abstain=<v> or min-accuracy=<v>";

    /// <summary>Reads one token of <c>--gate</c>.</summary>
    /// <param name="token">The token, <c>name=value</c>.</param>
    /// <exception cref="EvalsException">
    /// The name is not one of the two, the value is missing or is not a number in [0, 1]; the message names the
    /// option and the token.
    /// </exception>
    public static GateConstraint Parse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        (string name, double value) = ConstraintToken.Split(_option, token, _expected);
        GateConstraintKind kind = name switch
        {
            "max-abstain" => GateConstraintKind.MaxAbstain,
            "min-accuracy" => GateConstraintKind.MinAccuracy,
            _ => throw ConstraintToken.Malformed(_option, token, _expected),
        };
        return new GateConstraint(kind, value);
    }

    /// <summary>The constraint as it is spelled on the command line, which is how a report names it.</summary>
    public override string ToString() =>
        ConstraintToken.Format(Kind == GateConstraintKind.MaxAbstain ? "max-abstain" : "min-accuracy", Value);
}
