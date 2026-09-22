using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Tests.Support;

// A provider double for the live loop: it answers by a function of the request, can wait first and can
// throw instead, and counts what it was asked. The delay honours the token, so a run's per-attempt
// timeout ends it the way it would end a real call.
internal sealed class ScriptedProvider : IDecisionProvider
{
    private static readonly ProviderCapabilities _everything = new(
        new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
        new HashSet<EvidenceKind> { EvidenceKind.Probability, EvidenceKind.Score },
        RawOutput: false,
        StructuredContext: true);

    private Func<DecisionRequest, ProviderResult> _answer =
        _ => throw new InvalidOperationException("The scripted provider has no answer.");
    private Func<DecisionRequest, TimeSpan> _delay = _ => TimeSpan.Zero;
    private Exception? _throws;
    private int _calls;
    private int _inFlight;
    private int _maxInFlight;

    public ScriptedProvider(string id = "scripted") => Id = id;

    public string Id { get; }

    public ProviderCapabilities Capabilities => _everything;

    public int Calls => Volatile.Read(ref _calls);

    // The most calls that were inside DecideAsync at one moment.
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public ScriptedProvider Returns(Func<DecisionRequest, ProviderResult> answer)
    {
        _answer = answer;
        return this;
    }

    public ScriptedProvider Delays(TimeSpan delay) => Delays(_ => delay);

    public ScriptedProvider Delays(Func<DecisionRequest, TimeSpan> delay)
    {
        _delay = delay;
        return this;
    }

    public ScriptedProvider Throws(Exception exception)
    {
        _throws = exception;
        return this;
    }

    public async Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        int inFlight = Interlocked.Increment(ref _inFlight);
        int seen = Volatile.Read(ref _maxInFlight);
        while (inFlight > seen)
        {
            int previous = Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen);
            if (previous == seen)
            {
                break;
            }

            seen = previous;
        }

        try
        {
            TimeSpan delay = _delay(request);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return _throws is null ? _answer(request) : throw _throws;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    // The row's own text, which a test uses to script an answer per row.
    public static string TextOf(DecisionRequest request) => SemanticContext.ToCanonicalText(request.Context);
}
