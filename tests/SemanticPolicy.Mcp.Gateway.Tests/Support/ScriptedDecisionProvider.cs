using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// A provider double: answers every request as scripted and keeps each one it received, so a test reads the context the
// gateway built. It declares structured context, so the context reaches it as the object the gateway built. A request
// whose context holds a held marker answers nothing until the test releases that marker, which is how a test fixes the
// order verdicts finish in and proves an evaluation was in flight.
internal sealed class ScriptedDecisionProvider : IDecisionProvider
{
    public const string Name = "scripted";

    private static readonly ProviderMetadata _metadata = new(Name, "scripted-model", 1);

    private readonly Lock _lock = new();
    private readonly List<DecisionRequest> _requests = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _arrivals = [];
    private readonly HashSet<string> _holding = new(StringComparer.Ordinal);
    private readonly List<(string Context, TaskCompletionSource Gate)> _held = [];
    private Func<DecisionRequest, ProviderResult> _answer = _ => Clear;

    public string Id => Name;

    public ProviderCapabilities Capabilities { get; } = new(
        new HashSet<DecisionType> { DecisionType.Boolean },
        new HashSet<EvidenceKind> { EvidenceKind.Probability },
        RawOutput: false,
        StructuredContext: true);

    // Read at the deny rung of Screens' policy.
    public static ProviderResult Flagged => Boolean(true, 0.95);

    // Read at the warn rung.
    public static ProviderResult Warned => Boolean(true, 0.6);

    // Read as Allow.
    public static ProviderResult Clear => Boolean(false, 0.05);

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

    public ScriptedDecisionProvider Answers(ProviderResult result) => Answers(_ => result);

    public ScriptedDecisionProvider Answers(Func<DecisionRequest, ProviderResult> answer)
    {
        _answer = answer;
        return this;
    }

    // Answers Flagged when the context holds the marker, Clear otherwise.
    public ScriptedDecisionProvider Flags(string marker) =>
        Answers(request => ContextOf(request).Contains(marker, StringComparison.Ordinal) ? Flagged : Clear);

    // Holds every request whose context holds the marker until Release(marker), honouring the request's token.
    public ScriptedDecisionProvider Holds(string marker)
    {
        lock (_lock)
        {
            _holding.Add(marker);
        }

        return this;
    }

    // Answers every request held on the marker, and stops holding new ones on it.
    public void Release(string marker)
    {
        List<TaskCompletionSource> gates = [];
        lock (_lock)
        {
            _holding.Remove(marker);
            _held.RemoveAll(held =>
            {
                if (!held.Context.Contains(marker, StringComparison.Ordinal))
                {
                    return false;
                }

                gates.Add(held.Gate);
                return true;
            });
        }

        foreach (TaskCompletionSource gate in gates)
        {
            gate.TrySetResult();
        }
    }

    // Completes once at least count requests have arrived.
    public Task WaitForCallsAsync(int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_requests.Count >= count)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _arrivals.Add((count, signal));
        }

        return signal.Task.WaitAsync(cancellationToken);
    }

    public async Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        string context = ContextOf(request);
        TaskCompletionSource? gate = null;
        List<TaskCompletionSource> reached = [];
        lock (_lock)
        {
            // Held before the arrival is signalled, so a test that waits for an arrival and then releases cannot miss it.
            if (_holding.Any(marker => context.Contains(marker, StringComparison.Ordinal)))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add((context, gate));
            }

            _requests.Add(request);
            _arrivals.RemoveAll(waiter =>
            {
                if (_requests.Count < waiter.Count)
                {
                    return false;
                }

                reached.Add(waiter.Signal);
                return true;
            });
        }

        foreach (TaskCompletionSource signal in reached)
        {
            signal.TrySetResult();
        }

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return _answer(request);
    }

    public static ProviderResult Failed(FailureKind kind) =>
        ProviderResult.Failed(DecisionType.Boolean, kind, "failure-a", _metadata);

    // The context as it reached the provider, compact and unescaped enough to search for a marker.
    public static string ContextOf(DecisionRequest request) =>
        JsonSerializer.Serialize(request.Context, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ProviderResult Boolean(bool value, double probabilityOfTrue)
    {
        Dictionary<string, double> probabilities = new(StringComparer.Ordinal)
        {
            ["true"] = probabilityOfTrue,
            ["false"] = 1 - probabilityOfTrue,
        };
        return new(
            DecisionType.Boolean,
            ProviderOutcome.Success,
            new BooleanValue(value),
            [new Evidence(EvidenceKind.Probability, probabilities)],
            _metadata);
    }
}
