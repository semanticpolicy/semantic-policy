using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using SemanticPolicy.Core.Tests.Support;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;
using static SemanticPolicy.Telemetry.SemanticPolicyTelemetry;

namespace SemanticPolicy.Core.Tests;

// Listeners are process-wide, so every test here filters what it recorded down to its own policy id
// and disposes its listener before it ends; the class runs its tests one at a time.
public sealed class PolicyEvaluatorTelemetryTests
{
    private static readonly SemanticContext _context = SemanticContext.FromText("part-a", "corr-1");

    [Fact]
    public async Task Evaluation_Emits_A_Parent_Activity_And_One_Child_Per_Attempt_With_The_Declared_Tags()
    {
        ScriptedProvider first = new ScriptedProvider("one").Returns(request =>
            request.Question == "question-a" ? Answer(0.5) : Answer(0.95));
        ScriptedProvider second = new ScriptedProvider("two").Delays(TimeSpan.FromSeconds(30), Answer(0.1));
        PolicyEvaluator evaluator = Evaluator(("first", first), ("second", second));
        Policy policy = Cascade("tel-activities");
        using ActivityRecorder recorder = new();
        using CancellationTokenSource deadline = Deadline();

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, deadline.Token);

        verdict.Effective.Should().Be(Verdict.Allow);
        verdict.Evaluated.Should().Be(Verdict.Deny);
        Activity parent = recorder.Stopped.Should().ContainSingle(activity =>
            activity.OperationName == EvaluateActivity && Equals(activity.GetTagItem(PolicyIdTag), "tel-activities"))
            .Subject;
        parent.DisplayName.Should().Be(EvaluateActivity);
        parent.Parent.Should().BeNull();
        Tags(parent).Should().Equal(new Dictionary<string, object?>
        {
            [PolicyIdTag] = "tel-activities",
            [PolicyModeTag] = "shadow",
            [EffectiveVerdictTag] = "allow",
            [EvaluatedVerdictTag] = "deny",
            [CorrelationIdTag] = "corr-1",
        });

        Activity[] children = [.. recorder.Stopped.Where(activity => activity.Parent == parent)];
        children.Should().HaveCount(3).And.AllSatisfy(child =>
        {
            child.OperationName.Should().Be(AttemptActivity);
            child.DisplayName.Should().Be(AttemptActivity);
            child.ParentId.Should().Be(parent.Id);
        });
        Activity aOnFirst = children.Single(child => Is(child, "a", "first"));
        Tags(aOnFirst).Should().Equal(new Dictionary<string, object?>
        {
            [RuleIdTag] = "a",
            [ProviderIdTag] = "first",
            [ProviderModelTag] = "scripted-model",
            [DecisionTypeTag] = "boolean",
            [OutcomeStatusTag] = "success",
            [MarginTag] = 0.0,
            [ChainMovedByTag] = "gate",
            [FallbackToTag] = "second",
        });
        Activity aOnSecond = children.Single(child => Is(child, "a", "second"));
        Tags(aOnSecond).Should().Equal(new Dictionary<string, object?>
        {
            [RuleIdTag] = "a",
            [ProviderIdTag] = "second",
            [DecisionTypeTag] = "boolean",
            [OutcomeStatusTag] = "failure",
            [OutcomeFailureKindTag] = "timeout",
        });
        Activity bOnFirst = children.Single(child => Is(child, "b", "first"));
        Tags(bOnFirst).Should().Equal(new Dictionary<string, object?>
        {
            [RuleIdTag] = "b",
            [ProviderIdTag] = "first",
            [ProviderModelTag] = "scripted-model",
            [DecisionTypeTag] = "boolean",
            [OutcomeStatusTag] = "success",
            [EvidenceKindTag] = "probability",
            [EvidenceValueTag] = 0.95,
            [ThresholdCrossedTag] = "deny",
        });

        // A child's span is the provider call: the attempt that outlived the budget ended when the budget
        // did, not when the evaluation did, and every child ended before its parent.
        foreach (Activity child in children)
        {
            (child.StartTimeUtc + child.Duration).Should().BeOnOrBefore(parent.StartTimeUtc + parent.Duration);
        }

        aOnSecond.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // A provider that throws ends the evaluation, not the trace: the sibling that had already answered
    // reaches the listener stopped, the same as the attempt that failed, and the exception is the
    // provider's own.
    [Fact]
    public async Task A_Faulted_Attempt_Leaves_Its_Answered_Siblings_Spans_Stopped()
    {
        InvalidOperationException fault = new("scripted fault");
        ScriptedProvider provider = new ScriptedProvider("one").Returns(request =>
            request.Question == "question-a" ? Answer(0.1) : throw fault);
        PolicyEvaluator evaluator = Evaluator(("first", provider), ("second", provider));
        Policy policy = Cascade("tel-faulted-round");
        using ActivityRecorder recorder = new();

        Func<Task> evaluate = () => evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        (await evaluate.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        Activity parent = recorder.Stopped.Should().ContainSingle(activity =>
            activity.OperationName == EvaluateActivity && Equals(activity.GetTagItem(PolicyIdTag), "tel-faulted-round"))
            .Subject;
        Activity[] children = [.. recorder.Stopped.Where(activity => activity.Parent == parent)];
        children.Should().HaveCount(2);
        children.Should().ContainSingle(child => Is(child, "a", "first"));
        children.Should().ContainSingle(child => Is(child, "b", "first"));
        Activity.Current.Should().BeNull();
    }

    [Fact]
    public async Task Evaluation_Without_A_Listener_Starts_No_Activity()
    {
        Activity? current = null;
        bool seen = false;
        ScriptedProvider provider = new ScriptedProvider().Returns(_ =>
        {
            current = Activity.Current;
            seen = true;
            return Answer(0.1);
        });
        PolicyEvaluator evaluator = Evaluator(("first", provider), ("second", provider));
        Policy policy = Cascade("tel-silent");

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(Verdict.Allow);
        seen.Should().BeTrue();
        current.Should().BeNull();
        Activity.Current.Should().BeNull();
    }


    [Fact]
    public async Task Evaluation_Metrics_Count_Evaluations_And_Attempts_With_Their_Tags()
    {
        ScriptedProvider first = new ScriptedProvider("one").Returns(request =>
            request.Question == "question-a" ? Answer(0.5) : Answer(0.95));
        ScriptedProvider second = new ScriptedProvider("two").Delays(TimeSpan.FromSeconds(30), Answer(0.1));
        PolicyEvaluator evaluator = Evaluator(("first", first), ("second", second));
        Policy policy = Cascade("tel-metrics");
        using MeasurementRecorder recorder = new();
        using CancellationTokenSource deadline = Deadline();

        await evaluator.EvaluateAsync(policy, _context, deadline.Token);

        Measurement evaluations = recorder.Measurements.Should().ContainSingle(measurement =>
            measurement.Instrument == EvaluationsInstrument && Equals(measurement.Tags[PolicyIdTag], "tel-metrics"))
            .Subject;
        evaluations.Value.Should().Be(1);
        evaluations.Tags.Should().Equal(new Dictionary<string, object?>
        {
            [PolicyIdTag] = "tel-metrics",
            [PolicyModeTag] = "shadow",
            [EvaluatedVerdictTag] = "deny",
        });

        Measurement duration = recorder.Measurements.Should().ContainSingle(measurement =>
            measurement.Instrument == EvaluationDurationInstrument
            && Equals(measurement.Tags[PolicyIdTag], "tel-metrics"))
            .Subject;
        duration.Unit.Should().Be("s");
        duration.Value.Should().BeGreaterThan(0).And.BeLessThan(5);
        duration.Tags.Should().Equal(new Dictionary<string, object?>
        {
            [PolicyIdTag] = "tel-metrics",
            [PolicyModeTag] = "shadow",
        });

        Measurement[] attempts = [.. recorder.Measurements.Where(measurement =>
            measurement.Instrument == AttemptsInstrument
            && measurement.Tags[ProviderIdTag] is "first" or "second")];
        attempts.Should().HaveCount(3).And.AllSatisfy(attempt => attempt.Value.Should().Be(1));
        attempts.Select(attempt => attempt.Tags).Should().BeEquivalentTo(
        [
            new Dictionary<string, object?>
            {
                [ProviderIdTag] = "first",
                [DecisionTypeTag] = "boolean",
                [OutcomeStatusTag] = "success",
            },
            new Dictionary<string, object?>
            {
                [ProviderIdTag] = "first",
                [DecisionTypeTag] = "boolean",
                [OutcomeStatusTag] = "success",
            },
            new Dictionary<string, object?>
            {
                [ProviderIdTag] = "second",
                [DecisionTypeTag] = "boolean",
                [OutcomeStatusTag] = "failure",
                [OutcomeFailureKindTag] = "timeout",
            },
        ]);
    }


    // The canaries are distinct synthetic strings placed everywhere content can enter — a text part, a
    // JSON part, the question, an option description, a raw response and a failure message — and nothing
    // telemetry or a configuration error emits may carry any of them. The test is the guard: it stays
    // green only while every tag value keeps coming from the trace and the ids.
    [Fact]
    public async Task No_Telemetry_Tag_Or_Exception_Message_Carries_Content()
    {
        string[] canaries =
        [
            "canary-text-7f3a", "canary-json-9b21", "canary-question-4c8d",
            "canary-option-e51f", "canary-raw-2a6b", "canary-message-d094",
        ];
        SemanticContext context = new(
            [
                ContextPart.Text("t", "canary-text-7f3a"),
                ContextPart.Json("j", JsonDocument.Parse("""{"k":"canary-json-9b21"}""").RootElement.Clone()),
            ],
            "corr-3");
        ScriptedProvider first = new ScriptedProvider("one")
            .Returns(ScriptedProvider.Failure(DecisionType.Choice, FailureKind.Unavailable, "canary-message-d094"));
        ScriptedProvider second = new ScriptedProvider("two").Returns(
            ScriptedProvider.Success(new ChoiceValue("deny"), ("deny", 0.9), ("allow", 0.1)) with
            {
                Raw = JsonDocument.Parse("""{"raw":"canary-raw-2a6b"}""").RootElement.Clone(),
            });
        PolicyEvaluator evaluator = Evaluator(("first", first), ("second", second));
        Policy policy = Policy.Define("tel-canaries")
            .Enforce()
            .Rule(Policy.Rule("route").Choice("canary-question-4c8d")
                .Option("allow", "canary-option-e51f allow", Verdict.Allow)
                .Option("deny", "canary-option-e51f deny", Verdict.Deny)
                .Build())
            .Using("first", b => b.WhenProbabilityMarginBelow(0.2))
            .Using("second", b => b.WhenProbabilityMarginBelow(0.2))
            .OnFailure(FailureBehavior.Fallback(Verdict.Escalate))
            .Build();
        Policy misconfigured = Policy.Define("tel-canaries-ghost")
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("canary-question-4c8d").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using("ghost", b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9))
            .OnFailure(FailureBehavior.Deny)
            .Build();
        using ActivityRecorder activities = new();
        using MeasurementRecorder measurements = new();

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, context, TestContext.Current.CancellationToken);
        Func<Task> evaluate = () =>
            evaluator.EvaluateAsync(misconfigured, context, TestContext.Current.CancellationToken);
        PolicyConfigurationException error = (await evaluate.Should().ThrowAsync<PolicyConfigurationException>()).Which;

        verdict.Evaluated.Should().Be(Verdict.Deny);
        Activity parent = activities.Stopped.Should().ContainSingle(activity =>
            Equals(activity.GetTagItem(PolicyIdTag), "tel-canaries")).Subject;
        activities.Stopped.Where(activity => activity.Parent == parent).Should().HaveCount(2);
        measurements.Measurements.Should().Contain(measurement => measurement.Instrument == AttemptsInstrument);
        IEnumerable<string?> emitted = activities.Stopped
            .SelectMany(activity => activity.TagObjects
                .SelectMany(tag => new[] { tag.Key, tag.Value?.ToString() })
                .Append(activity.DisplayName)
                .Append(activity.OperationName))
            .Concat(measurements.Measurements
                .SelectMany(measurement => measurement.Tags
                    .SelectMany(tag => new[] { tag.Key, tag.Value?.ToString() })))
            .Append(error.Message);
        string.Join(Environment.NewLine, emitted).Should().NotContainAny(canaries);
        error.ProviderId.Should().Be("ghost");
    }

    private static bool Is(Activity child, string ruleId, string providerId) =>
        Equals(child.GetTagItem(RuleIdTag), ruleId) && Equals(child.GetTagItem(ProviderIdTag), providerId);

    private static Dictionary<string, object?> Tags(Activity activity) =>
        activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);

    private static PolicyEvaluator Evaluator(params (string Name, ScriptedProvider Provider)[] providers) =>
        new(providers.Select(entry => new ProviderRegistration(entry.Name, entry.Provider)), []);

    // A hold that is never released, or a call that is never answered, fails the test instead of hanging it.
    private static CancellationTokenSource Deadline()
    {
        CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return deadline;
    }

    private static ProviderResult Answer(double probability) =>
        ScriptedProvider.Success(
            new BooleanValue(probability >= 0.5),
            ("true", probability),
            ("false", 1 - probability));

    // Shadow, a 100 ms budget and Fallback(then: Deny) over "first" then "second". Rule "a" is gated on
    // "first" only; rule "b" is not gated. Both are Warn at 0.6 / Deny at 0.9 on probability everywhere.
    private static Policy Cascade(string id) =>
        Policy.Define(id)
            .Shadow()
            .Rule(Policy.Rule("a").Boolean("question-a").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Rule(Policy.Rule("b").Boolean("question-b").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using("first", b => b
                .ForRule("a", p => p
                    .WarnAboveProbability(0.6)
                    .DenyAboveProbability(0.9)
                    .WhenProbabilityMarginBelow(0.2))
                .ForRule("b", p => p.WarnAboveProbability(0.6).DenyAboveProbability(0.9)))
            .Using("second", b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9))
            .Budget(TimeSpan.FromMilliseconds(100))
            .OnFailure(FailureBehavior.Fallback(Verdict.Deny))
            .Build();

    // Records every SemanticPolicy activity as it stops — the moment an exporter reads its tags — and
    // stops recording when disposed.
    private sealed class ActivityRecorder : IDisposable
    {
        private readonly List<Activity> _stopped = [];
        private readonly ActivityListener _listener;

        public ActivityRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_stopped)
                    {
                        _stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Stopped
        {
            get
            {
                lock (_stopped)
                {
                    return [.. _stopped];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measurement(string Instrument, string? Unit, double Value, Dictionary<string, object?> Tags);

    // Records every measurement on the SemanticPolicy meter, whichever instrument and numeric type,
    // and stops recording when disposed.
    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly List<Measurement> _measurements = [];
        private readonly MeterListener _listener = new();

        public MeasurementRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> copied = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add(new Measurement(instrument.Name, instrument.Unit, value, copied));
            }
        }
    }
}
