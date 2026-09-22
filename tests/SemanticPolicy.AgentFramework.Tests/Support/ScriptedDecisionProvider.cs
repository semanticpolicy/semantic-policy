using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.AgentFramework.Tests.Support;

/// <summary>
/// A provider double for the guard layer's tests: answers every request as scripted and keeps each
/// request it received, so a test reads the context the layer built straight off the wire. It
/// declares structured context, so an object context reaches it as an object.
/// </summary>
internal sealed class ScriptedDecisionProvider : IDecisionProvider
{
    private static readonly ProviderMetadata _metadata = new("scripted", "scripted-model", 1);

    private readonly Lock _lock = new();
    private readonly List<DecisionRequest> _requests = [];
    private Func<DecisionRequest, CancellationToken, Task<ProviderResult>> _script =
        (_, _) => throw new InvalidOperationException("The scripted provider has no script.");

    public string Id => "scripted";

    public ProviderCapabilities Capabilities { get; } = new(
        new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
        new HashSet<EvidenceKind> { EvidenceKind.Probability },
        RawOutput: false,
        StructuredContext: true);

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<DecisionRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answer every request with the result.</summary>
    public ScriptedDecisionProvider Returns(ProviderResult result) => Returns(_ => result);

    /// <summary>Answer every request with the result the function builds for it.</summary>
    public ScriptedDecisionProvider Returns(Func<DecisionRequest, ProviderResult> result)
    {
        _script = (request, _) => Task.FromResult(result(request));
        return this;
    }

    /// <summary>Answer nothing until the token is cancelled, then surface the cancellation.</summary>
    public ScriptedDecisionProvider AnswersOnlyOnCancellation()
    {
        _script = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay ended without a cancellation.");
        };
        return this;
    }

    public Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }

        return _script(request, cancellationToken);
    }

    /// <summary>A Boolean answer with the probability of its being true, the rest of the mass on false.</summary>
    public static ProviderResult Boolean(bool value, double probabilityOfTrue)
    {
        Dictionary<string, double> probabilities = new(StringComparer.Ordinal)
        {
            ["true"] = probabilityOfTrue,
            ["false"] = 1 - probabilityOfTrue,
        };
        Evidence evidence = new(EvidenceKind.Probability, probabilities);
        return new(DecisionType.Boolean, ProviderOutcome.Success, new BooleanValue(value), [evidence], _metadata);
    }

    public static ProviderResult Abstain() =>
        new(DecisionType.Boolean, ProviderOutcome.Abstain(message: null), Value: null, Evidence: [], _metadata);

    public static ProviderResult Failure(FailureKind kind) =>
        ProviderResult.Failed(DecisionType.Boolean, kind, "scripted", _metadata);
}
