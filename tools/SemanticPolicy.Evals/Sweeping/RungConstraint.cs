using System.Globalization;
using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Sweeping;

/// <summary>Which rate a rung constraint bounds, and so which end of its feasible thresholds it recommends.</summary>
public enum ConstraintKind
{
    /// <summary><c>min-recall=x</c>: recall of at least x; the highest threshold that keeps it is recommended.</summary>
    MinRecall,

    /// <summary><c>max-fpr=y</c>: a false-positive rate of at most y; the lowest threshold that keeps it.</summary>
    MaxFpr,

    /// <summary><c>min-precision=z</c>: precision of at least z; the lowest threshold that keeps it.</summary>
    MinPrecision,
}

/// <summary>
/// A bound the user put on one ladder rung, as given to <c>--warn</c>, <c>--escalate</c> or <c>--deny</c>. A
/// threshold is only ever recommended against a stated bound like this one, never by a score the tool picked,
/// and each rung is bounded on its own because a missed flag and a false one cost differently at each rung.
/// </summary>
/// <param name="Rung">The ladder rung the bound applies to.</param>
/// <param name="Kind">The rate that is bounded.</param>
/// <param name="Value">The bound, in [0, 1].</param>
public sealed record RungConstraint(Verdict Rung, ConstraintKind Kind, double Value)
{
    private const string _expected = "min-recall=<v>, max-fpr=<v> or min-precision=<v>";

    /// <summary>Reads one token of a rung option.</summary>
    /// <param name="rung">The rung whose option the token was given to: Warn, Escalate or Deny.</param>
    /// <param name="token">The token, <c>name=value</c>.</param>
    /// <exception cref="EvalsException">
    /// The name is not one of the three, the value is missing or is not a number in [0, 1]; the message names
    /// the option and the token.
    /// </exception>
    public static RungConstraint Parse(Verdict rung, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (rung is not (Verdict.Warn or Verdict.Escalate or Verdict.Deny))
        {
            throw new ArgumentOutOfRangeException(nameof(rung), rung, "Only a ladder rung takes a constraint.");
        }

        string option = "--" + Names.Camel(rung);
        (string name, double value) = ConstraintToken.Split(option, token, _expected);
        ConstraintKind kind = name switch
        {
            "min-recall" => ConstraintKind.MinRecall,
            "max-fpr" => ConstraintKind.MaxFpr,
            "min-precision" => ConstraintKind.MinPrecision,
            _ => throw ConstraintToken.Malformed(option, token, _expected),
        };
        return new RungConstraint(rung, kind, value);
    }

    /// <summary>The constraint as it is spelled on the command line, which is how a report names it.</summary>
    public override string ToString()
    {
        string name = Kind switch
        {
            ConstraintKind.MinRecall => "min-recall",
            ConstraintKind.MaxFpr => "max-fpr",
            _ => "min-precision",
        };
        return ConstraintToken.Format(name, Value);
    }
}

// The name=value shape both kinds of constraint share. Only the value is read here; each kind checks the name
// against its own vocabulary, so a gate name given to a rung option is rejected by the option it was given to.
internal static class ConstraintToken
{
    internal static (string Name, double Value) Split(string option, string token, string expected)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0
            && double.TryParse(token.AsSpan(equals + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value is >= 0 and <= 1)
        {
            return (token[..equals], value);
        }

        throw Malformed(option, token, expected);
    }

    internal static EvalsException Malformed(string option, string token, string expected) =>
        new($"{option} '{token}' is not a constraint; expected {expected}, with <v> a number from 0 to 1.");

    internal static string Format(string name, double value) =>
        $"{name}={value.ToString(CultureInfo.InvariantCulture)}";
}
