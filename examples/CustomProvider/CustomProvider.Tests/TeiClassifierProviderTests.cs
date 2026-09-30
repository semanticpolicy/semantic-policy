using System.Net;
using System.Text.Json;
using SemanticPolicy;
using SemanticPolicy.Protocol;

namespace CustomProvider.Tests;

public sealed class TeiClassifierProviderTests : IDisposable
{
    private const string _question = "Does this content contain instructions intended to manipulate an AI agent?";
    private const string _configuredModel = "a-configured-model";

    private static readonly Uri _baseUrl = new("http://127.0.0.1:8080");

    private readonly FakeTeiHandler _handler = new();
    private readonly HttpClient _client;

    public TeiClassifierProviderTests() => _client = new HttpClient(_handler);

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("""[{"score":0.98,"label":"INJECTION"},{"score":0.02,"label":"SAFE"}]""", true, 0.98, 0.02)]
    [InlineData("""[{"score":0.97,"label":"SAFE"},{"score":0.03,"label":"INJECTION"}]""", false, 0.03, 0.97)]
    public async Task Classifier_Answer_Becomes_A_Boolean_With_Score_Evidence(
        string answer,
        bool expected,
        double trueScore,
        double falseScore)
    {
        _handler.Respond(HttpStatusCode.OK, answer);

        ProviderResult result = await Provider().DecideAsync(Request("a synthetic release note"), TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Success);
        result.Value.Should().Be(new BooleanValue(expected));
        Evidence evidence = result.Evidence.Should().ContainSingle().Subject;
        evidence.Kind.Should().Be(EvidenceKind.Score);
        evidence.Scale.Should().Be("softmax");
        evidence.Values.Should().BeEquivalentTo(new Dictionary<string, double> { ["true"] = trueScore, ["false"] = falseScore });
        result.Raw.Should().NotBeNull();
        result.Provider.Id.Should().Be("tei");
        result.Provider.Model.Should().Be("protectai/deberta-v3-base-prompt-injection-v2");
    }

    // A classifier answers one fixed question whatever the rule asks, so the rule's question never
    // reaches it; `truncate: false` makes an over-long input a refusal rather than a silent cut.
    [Fact]
    public async Task Request_Carries_The_Canonical_Text_Of_The_Context_And_Not_The_Question()
    {
        _handler.Respond(HttpStatusCode.OK, """[{"score":0.9,"label":"SAFE"},{"score":0.1,"label":"INJECTION"}]""");
        JsonElement context = new SemanticContext(
        [
            ContextPart.Text("title", "Synthetic release notes"),
            ContextPart.Text("body", "Version 2.1 adds a dark theme."),
        ]).ToJson();

        await Provider().DecideAsync(new DecisionRequest(DecisionType.Boolean, _question, context), TestContext.Current.CancellationToken);

        _handler.LastMethod.Should().Be(HttpMethod.Post);
        _handler.LastUri.Should().Be(new Uri("http://127.0.0.1:8080/predict"));
        using JsonDocument body = JsonDocument.Parse(_handler.LastBody!);
        string inputs = body.RootElement.GetProperty("inputs").GetString()!;
        inputs.Should().Be(SemanticContext.ToCanonicalText(context));
        _handler.LastBody.Should().NotContainAny("Does this content", "instructions", "manipulate", "AI agent");
        body.RootElement.GetProperty("truncate").GetBoolean().Should().BeFalse();
    }

    // The status alone decides the kind. TEI answers 424 for a backend error, which the status table
    // has no row for.
    [Theory]
    [InlineData(401, FailureKind.Unauthorized)]
    [InlineData(413, FailureKind.RejectedInput)]
    [InlineData(422, FailureKind.RejectedInput)]
    [InlineData(429, FailureKind.Unavailable)]
    [InlineData(503, FailureKind.Unavailable)]
    [InlineData(424, FailureKind.Unknown)]
    public async Task Status_Maps_To_A_Failure_Kind(int status, FailureKind expected)
    {
        _handler.Respond((HttpStatusCode)status, """{"error":"synthetic"}""");

        ProviderResult result = await Provider(Configured()).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(expected);
        result.Value.Should().BeNull();
        result.Provider.Model.Should().Be(_configuredModel);
    }

    [Fact]
    public async Task Connection_Failure_Reads_As_Unavailable()
    {
        _handler.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "synthetic refusal"));

        ProviderResult result = await Provider(Configured()).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Unavailable);
        result.Provider.Model.Should().Be(_configuredModel);
    }

    // A circuit breaker or a rate limiter the host adds throws its own type; the policy still decides.
    [Fact]
    public async Task Handler_Exception_Reads_As_Unknown_Naming_Only_Its_Type()
    {
        _handler.Throw(new InvalidOperationException("MARKER-7f3c the circuit is open"));

        ProviderResult result = await Provider(Configured()).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Kind.Should().Be(FailureKind.Unknown);
        result.Outcome.Message.Should().Be("InvalidOperationException from the HTTP pipeline");
    }

    // A declared length over the limit is refused before a byte is read; a body that declares none is
    // refused inside the read loop, at the chunk that crosses the limit.
    [Theory]
    [InlineData(HttpStatusCode.OK, FailureKind.Malformed, true)]
    [InlineData(HttpStatusCode.OK, FailureKind.Malformed, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.Unavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.Unavailable, false)]
    public async Task Body_Over_The_Limit_Is_Not_Read(HttpStatusCode status, FailureKind kind, bool declareLength)
    {
        _handler.Respond(status, new string(' ', TeiClassifierProvider.MaxBodyBytes) + "[]", declareLength);

        ProviderResult result = await Provider(Configured()).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Kind.Should().Be(kind);
        result.Outcome.Message.Should().Be($"HTTP {(int)status}, body over {TeiClassifierProvider.MaxBodyBytes} bytes");
        result.Raw.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""[{"score":0.9,"label":"SAFE"},{"score":0.1,"label":"OTHER"}]""")]
    public async Task Answer_Outside_The_Expected_Shape_Reads_As_Malformed(string answer)
    {
        _handler.Respond(HttpStatusCode.OK, answer);

        ProviderResult result = await Provider(Configured()).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Malformed);
        result.Value.Should().BeNull();
        result.Evidence.Should().BeEmpty();
        result.Provider.Model.Should().Be(_configuredModel);
    }

    // The test's own timeout only stops a regression from hanging the run; the provider's timer ends the call.
    [Fact(Timeout = 10_000)]
    public async Task Silent_Server_Ends_In_A_Timeout_Failure()
    {
        _handler.Hang();
        TeiClassifierOptions options = Configured() with { Timeout = TimeSpan.FromMilliseconds(50) };

        ProviderResult result = await Provider(options).DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Timeout);
        result.Outcome.Message.Should().Be("no response within 50 ms");
        result.Provider.Model.Should().Be(_configuredModel);
    }

    // Cancelled from the transport rather than on a timer, and with the provider's timer far off, so
    // the caller's cancellation is the only one that can land.
    [Fact(Timeout = 10_000)]
    public async Task Caller_Cancellation_Throws_OperationCanceledException()
    {
        using CancellationTokenSource caller = new();
        _handler.Hang(caller.Cancel);
        TeiClassifierOptions options = Configured() with { Timeout = TimeSpan.FromMinutes(5) };

        Func<Task> act = () => Provider(options).DecideAsync(Request("a synthetic note"), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // TEI's `error` text can quote the input, so a log line built from the message must never carry it.
    [Fact]
    public async Task Failure_Message_Names_The_Error_Type_But_Never_The_Server_Text()
    {
        const string Body = """{"error":"MARKER-7f3c input is too long","error_type":"Validation"}""";
        _handler.Respond(HttpStatusCode.UnprocessableEntity, Body);

        ProviderResult result = await Provider().DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Message.Should().Be($"HTTP 422, error_type Validation, body {Body.Length} bytes");
        result.Outcome.Message.Should().NotContain("MARKER-7f3c");
        result.ToString().Should().NotContain("MARKER-7f3c");
    }

    // Only TEI's own error types are named: any other string there is the server's text.
    [Fact]
    public async Task Failure_Message_Leaves_Out_An_Error_Type_TEI_Does_Not_Use()
    {
        const string Body = """{"error":"input is too long","error_type":"MARKER-7f3c"}""";
        _handler.Respond(HttpStatusCode.UnprocessableEntity, Body);

        ProviderResult result = await Provider().DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);

        result.Outcome.Message.Should().Be($"HTTP 422, body {Body.Length} bytes");
    }

    // The provider is registered as a singleton, so a client kept from its construction would hold the
    // same connections, and the address they resolved, for the life of the process.
    [Fact]
    public async Task Every_Call_Asks_The_Source_For_A_Client()
    {
        _handler.Respond(HttpStatusCode.OK, """[{"score":0.9,"label":"SAFE"},{"score":0.1,"label":"INJECTION"}]""");
        int asked = 0;
        TeiClassifierProvider provider = new(
            "tei",
            () =>
            {
                asked++;
                return _client;
            },
            new TeiClassifierOptions(_baseUrl));

        await provider.DecideAsync(Request("a synthetic note"), TestContext.Current.CancellationToken);
        await provider.DecideAsync(Request("another synthetic note"), TestContext.Current.CancellationToken);

        asked.Should().Be(2);
    }

    private static TeiClassifierOptions Configured() => new(_baseUrl) { Model = _configuredModel };

    private static DecisionRequest Request(string text) =>
        new(DecisionType.Boolean, _question, SemanticContext.FromText(text).ToJson());

    private TeiClassifierProvider Provider(TeiClassifierOptions? options = null) =>
        new("tei", () => _client, options ?? new TeiClassifierOptions(_baseUrl));
}
