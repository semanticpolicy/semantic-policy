using System.Net;
using System.Text;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.Http;

namespace SemanticPolicy.Providers.ContractTests.Http;

public sealed class HttpProviderTests
{
    // Score evidence with both ends of a Boolean and a third key the request never offered: the marker.
    private const string _keyedOutside =
        $$$"""[{"kind":"score","values":{"true":0.91,"false":0.09,"{{{ProviderHarness.Marker}}}":0.5}}]""";

    // A provider built by hand never reads the environment, so options that name only a variable would
    // send no bearer and report no error; the constructor refuses them instead.
    [Fact]
    public void Constructor_Rejects_A_Key_Variable_It_Would_Never_Read()
    {
        using HttpClient client = new(new ScriptedHttpMessageHandler());
        HttpProviderOptions options = new()
        {
            BaseUrl = HttpHarness.BaseUrl,
            Model = HttpHarness.Model,
            Types = [DecisionType.Boolean],
            Evidence = [EvidenceKind.Score],
            StructuredContext = false,
            ApiKeyVariable = $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}",
        };

        Action variableOnly = () => _ = new HttpProvider(client, options);

        variableOnly.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(HttpProviderOptions.ApiKeyVariable));

        options.ApiKey = "test-key-not-a-credential";
        Action withKey = () => _ = new HttpProvider(client, options);

        withKey.Should().NotThrow();
    }

    // The harness's context is an object of two parts, so its canonical text is two blocks.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wire_Context_Follows_StructuredContext(bool structuredContext)
    {
        HttpHarness harness = new(options => options.StructuredContext = structuredContext);
        harness.ScriptSuccess(DecisionType.Boolean);
        DecisionRequest request = harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker);

        await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        JsonElement sent = harness.LastBody;
        sent.GetProperty("protocol").GetString().Should().Be("semanticpolicy/v0");
        sent.GetProperty("type").GetString().Should().Be("boolean");
        sent.GetProperty("question").GetString().Should().Be(request.Question);
        sent.GetProperty("criteria").GetProperty("true").GetString().Should().Be(request.Criteria!.True);
        JsonElement context = sent.GetProperty("context");
        if (structuredContext)
        {
            JsonElement.DeepEquals(context, request.Context).Should().BeTrue();
        }
        else
        {
            context.ValueKind.Should().Be(JsonValueKind.String);
            context.GetString().Should()
                .Be(SemanticContext.ToCanonicalText(request.Context))
                .And.Be($"text:\nA synthetic context that mentions {ProviderHarness.Marker}.\n\nsource:\ntest");
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765", null, "http://127.0.0.1:8765/v0/decide")]
    [InlineData("http://127.0.0.1:8765", "/classify", "http://127.0.0.1:8765/classify")]
    [InlineData("https://gateway.test/semantic/", null, "https://gateway.test/semantic/v0/decide")]
    public async Task Request_Is_Posted_To_The_Configured_Path_Under_The_Base_Url(string baseUrl, string? path, string expected)
    {
        HttpHarness harness = new(options =>
        {
            options.BaseUrl = new Uri(baseUrl);
            if (path is not null)
            {
                options.Path = path;
            }
        });
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        RecordedRequest sent = harness.LastRequest;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be(new Uri(expected));
        sent.Header("Content-Type").Should().Be("application/json");
        sent.Header("User-Agent").Should().StartWith("SemanticPolicy.Providers.Http/");
    }

    [Theory]
    [InlineData(null, 20_000, true)]
    [InlineData(100, 100, true)]
    [InlineData(100, 101, false)]
    public async Task Context_Length_Against_MaxContextLength_Decides_Whether_The_Server_Is_Called(
        int? limit,
        int length,
        bool called)
    {
        HttpHarness harness = new(options => options.MaxContextLength = limit);
        harness.ScriptSuccess(DecisionType.Boolean);
        DecisionRequest request = new(
            DecisionType.Boolean,
            "Is the synthetic text long?",
            JsonSerializer.SerializeToElement(new string('a', length)));

        ProviderResult result = await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        if (called)
        {
            harness.RequestCount.Should().Be(1);
            result.Outcome.Should().Be(ProviderOutcome.Success);
        }
        else
        {
            harness.RequestCount.Should().Be(0);
            result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
            result.Outcome.Kind.Should().Be(FailureKind.RejectedInput);
        }
    }

    // The server names its own id and latency, and sends usage and extra; the client reports its own id
    // and measurement, and keeps what it does not relay in Raw only.
    [Theory]
    [InlineData(OutcomeStatus.Success)]
    [InlineData(OutcomeStatus.Abstain)]
    public async Task Server_Answer_Is_Relayed_Under_The_Client_Id(OutcomeStatus status)
    {
        HttpHarness harness = new(options => options.Id = "v0-client");
        const string evidence = """
            [{"kind":"score","scale":"sigmoid","values":{"true":0.91,"false":0.09}},
             {"kind":"logit","values":{"true":2.31,"false":-2.31}}]
            """;
        const string provider = """
            {"id":"v0-server","model":"v0-test-server-model","latencyMs":987654,"requestId":"req-0042",
             "usage":{"tokens":17},"extra":{"note":"server-shaped"}}
            """;
        string body = status == OutcomeStatus.Success
            ? HttpHarness.Answer(evidence: evidence, provider: provider)
            : HttpHarness.Answer(
                outcome: $$"""{"status":"abstain","message":"declined {{ProviderHarness.Marker}}"}""",
                value: null,
                evidence: evidence,
                provider: provider);
        harness.Handler.Respond(HttpStatusCode.OK, body);

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        if (status == OutcomeStatus.Success)
        {
            result.Outcome.Should().Be(ProviderOutcome.Success);
            result.Value.Should().Be(new BooleanValue(true));
        }
        else
        {
            result.Outcome.Should().Be(new ProviderOutcome(OutcomeStatus.Abstain));
            result.Value.Should().BeNull();
        }

        Evidence score = result.Evidence.Should().ContainSingle().Which;
        score.Kind.Should().Be(EvidenceKind.Score);
        score.Scale.Should().Be("sigmoid");
        score.Values.Should().BeEquivalentTo(new Dictionary<string, double> { ["true"] = 0.91, ["false"] = 0.09 });
        result.Provider.Id.Should().Be("v0-client");
        result.Provider.Model.Should().Be(HttpHarness.ServerModel);
        result.Provider.RequestId.Should().Be("req-0042");
        result.Provider.LatencyMs.Should().BePositive().And.BeLessThan(987654);
        result.Provider.Usage.Should().BeNull();
        result.Provider.Extra.Should().BeNull();
        JsonElement.DeepEquals(result.Raw!.Value, JsonDocument.Parse(body).RootElement).Should().BeTrue();
    }

    // Only the declared kinds are checked: an undeclared kind is dropped unread, whatever its keys.
    [Fact]
    public async Task Undeclared_Evidence_Is_Dropped_Without_Being_Checked()
    {
        HttpHarness harness = new();
        harness.Handler.Respond(
            HttpStatusCode.OK,
            HttpHarness.Answer(evidence: $$$"""
                [{"kind":"score","values":{"true":0.91,"false":0.09}},
                 {"kind":"logit","values":{"{{{ProviderHarness.Marker}}}":2.31}}]
                """));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Evidence.Should().ContainSingle().Which.Kind.Should().Be(EvidenceKind.Score);
        result.ToString().Should().NotContain(ProviderHarness.Marker);
    }

    // Member names are read as the protocol spells them. A reader that ignored case would take
    // "Evidence" for evidence, past the check that every entry names its kind, and an entry without one
    // would reach a probability threshold as the enum's first value.
    [Fact]
    public async Task Member_Named_In_Another_Case_Is_Not_Read()
    {
        HttpHarness harness = new(options => options.Evidence = [EvidenceKind.Score, EvidenceKind.Probability]);
        harness.Handler.Respond(
            HttpStatusCode.OK,
            HttpHarness.Answer(evidence: null).Replace(
                "\"provider\":",
                "\"Evidence\":[{\"values\":{\"true\":0.99,\"false\":0.01}}],\"provider\":",
                StringComparison.Ordinal));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Evidence.Should().BeEmpty();
    }

    public static TheoryData<string> ServerStringsThatAreNotIdentifiers =>
    [
        "a model with a space", "a request id with a line break", "a scale over 200 characters", "an empty model",
    ];

    // The scale, the model and the request id are the strings a relayed result carries besides its
    // value and keys, so each is kept only as an identifier: printable ASCII without a space, 1 to 200
    // characters. A model that is not one gives way to the configured model; the others are dropped.
    [Theory]
    [MemberData(nameof(ServerStringsThatAreNotIdentifiers))]
    public async Task Server_String_That_Is_Not_An_Identifier_Is_Not_Relayed(string shape)
    {
        HttpHarness harness = new();
        string marker = ProviderHarness.Marker;
        (string evidence, string provider) = shape switch
        {
            "a model with a space" => (
                """[{"kind":"score","scale":"sigmoid","values":{"true":0.91,"false":0.09}}]""",
                $$"""{"id":"v0-server","model":"model {{marker}}","latencyMs":12,"requestId":"req-0042"}"""),
            "a request id with a line break" => (
                """[{"kind":"score","scale":"sigmoid","values":{"true":0.91,"false":0.09}}]""",
                $$"""{"id":"v0-server","model":"{{HttpHarness.ServerModel}}","latencyMs":12,"requestId":"req\n{{marker}}"}"""),
            "a scale over 200 characters" => (
                $$$"""[{"kind":"score","scale":"{{{new string('s', 201)}}}","values":{"true":0.91,"false":0.09}}]""",
                $$"""{"id":"v0-server","model":"{{HttpHarness.ServerModel}}","latencyMs":12,"requestId":"req-0042"}"""),
            "an empty model" => (
                """[{"kind":"score","scale":"sigmoid","values":{"true":0.91,"false":0.09}}]""",
                """{"id":"v0-server","model":"","latencyMs":12,"requestId":"req-0042"}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        harness.Handler.Respond(HttpStatusCode.OK, HttpHarness.Answer(evidence: evidence, provider: provider));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Evidence.Should().ContainSingle().Which.Scale.Should().Be(
            shape == "a scale over 200 characters" ? null : "sigmoid");
        result.Provider.Model.Should().Be(
            shape is "a model with a space" or "an empty model" ? HttpHarness.Model : HttpHarness.ServerModel);
        result.Provider.RequestId.Should().Be(shape == "a request id with a line break" ? null : "req-0042");
        result.ToString().Should().NotContain(ProviderHarness.Marker);
    }

    [Theory]
    [InlineData("a success whose provider has no model")]
    [InlineData("a bare 503")]
    [InlineData("a v0 failure body naming another model")]
    [InlineData("a context over MaxContextLength")]
    public async Task Result_Without_A_Server_Model_Reports_The_Configured_Model(string answer)
    {
        HttpHarness harness = new(options => options.MaxContextLength = answer == "a context over MaxContextLength" ? 10 : null);
        switch (answer)
        {
            case "a success whose provider has no model":
                harness.Handler.Respond(HttpStatusCode.OK, HttpHarness.Answer(provider: """{"id":"v0-server","latencyMs":12}"""));
                break;
            case "a bare 503":
                harness.Handler.Respond(HttpStatusCode.ServiceUnavailable);
                break;
            case "a v0 failure body naming another model":
                harness.Handler.Respond(HttpStatusCode.InternalServerError, HttpHarness.FailureBody("unavailable", "not loaded"));
                break;
            case "a context over MaxContextLength":
                harness.ScriptSuccess(DecisionType.Boolean);
                break;
        }

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Provider.Model.Should().Be(HttpHarness.Model);
    }

    public static TheoryData<string> OutsideTheContract =>
    [
        "a v0 failure", "HTML", "another JSON shape", "another protocol", "no protocol", "a JSON null",
        "no outcome", "no provider", "no type", "no outcome.status", "an evidence entry without kind",
        "an entry without values", "a null entry", "a result of another type", "evidence keyed outside the answers",
        "an abstain with evidence keyed outside the answers", "a probability above 1", "a negative probability",
        "a number as a string", "NaN as a string", "a number beyond a double", "two entries of one declared kind",
    ];

    // Every case but HTML is JSON, and the registration declares Probability: an entry without a kind
    // must not reach a probability threshold as the enum's first value, and neither may a number
    // outside [0, 1] or under a key the request did not offer. A value is a finite JSON number, since
    // NaN passes no threshold and so reads as allowed, and a kind comes once, since a threshold reads
    // only the first entry of its kind.
    [Theory]
    [MemberData(nameof(OutsideTheContract))]
    public async Task Answer_On_200_Outside_The_Contract_Reads_As_Malformed(string shape)
    {
        HttpHarness harness = new(options => options.Evidence = [EvidenceKind.Score, EvidenceKind.Probability]);
        string body = shape switch
        {
            "a v0 failure" => HttpHarness.FailureBody("unavailable", $"model not loaded for {ProviderHarness.Marker}"),
            "HTML" => HttpHarness.Html(ProviderHarness.Marker),
            "another JSON shape" => $$$"""{"label":"SAFE","score":0.98,"note":"{{{ProviderHarness.Marker}}}"}""",
            "another protocol" => HttpHarness.Answer(protocol: "\"semanticpolicy/v1\""),
            "no protocol" => HttpHarness.Answer(protocol: null),
            "a JSON null" => "null",
            "no outcome" => HttpHarness.Answer(outcome: null),
            "no provider" => HttpHarness.Answer(provider: null),
            "no type" => HttpHarness.Answer(type: null),
            "no outcome.status" => HttpHarness.Answer(outcome: "{}"),
            "an evidence entry without kind" => HttpHarness.Answer(evidence: """[{"values":{"true":0.91}}]"""),
            "an entry without values" => HttpHarness.Answer(evidence: """[{"kind":"score"}]"""),
            "a null entry" => HttpHarness.Answer(evidence: "[null]"),
            "a result of another type" => HttpHarness.Answer(type: "\"choice\"", value: "\"allow\""),
            "evidence keyed outside the answers" => HttpHarness.Answer(evidence: _keyedOutside),
            "an abstain with evidence keyed outside the answers" => HttpHarness.Answer(
                outcome: """{"status":"abstain"}""",
                value: null,
                evidence: _keyedOutside),
            "a probability above 1" => HttpHarness.Answer(evidence: """[{"kind":"probability","values":{"true":1.2}}]"""),
            "a negative probability" => HttpHarness.Answer(evidence: """[{"kind":"probability","values":{"true":-0.1}}]"""),
            "a number as a string" => HttpHarness.Answer(evidence: """[{"kind":"score","values":{"true":"0.91","false":0.09}}]"""),
            "NaN as a string" => HttpHarness.Answer(evidence: """[{"kind":"score","values":{"true":"NaN","false":"NaN"}}]"""),
            "a number beyond a double" => HttpHarness.Answer(evidence: """[{"kind":"score","values":{"true":1e400,"false":0.09}}]"""),
            "two entries of one declared kind" => HttpHarness.Answer(evidence: """
                [{"kind":"score","values":{"true":0.09,"false":0.91}},
                 {"kind":"score","values":{"true":0.91,"false":0.09}}]
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        harness.Handler.Respond(HttpStatusCode.OK, body, shape == "HTML" ? "text/html" : "application/json");

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Malformed);
        result.Outcome.Message.Should().NotContain(ProviderHarness.Marker);
        result.Value.Should().BeNull();
        result.Evidence.Should().BeEmpty();
        result.Provider.Model.Should().Be(HttpHarness.Model);
        if (shape == "HTML")
        {
            result.Raw.Should().BeNull();
        }
        else
        {
            JsonElement.DeepEquals(result.Raw!.Value, JsonDocument.Parse(body).RootElement).Should().BeTrue();
        }
    }

    public static TheoryData<string> ValuesTheRequestDoesNotAllow =>
    [
        "a success without a value", "a string on a Boolean question", "an option the request does not offer",
        "a level the request does not have", "an index that does not match its level", "an abstain with a value",
    ];

    // A value is text the server wrote, so it must be one the request offered: anything else would
    // reach the result's JSON and its ToString.
    [Theory]
    [MemberData(nameof(ValuesTheRequestDoesNotAllow))]
    public async Task Value_The_Request_Does_Not_Allow_Is_Never_Relayed(string shape)
    {
        HttpHarness harness = new();
        string marker = $"\"{ProviderHarness.Marker}\"";
        (DecisionType type, string body) = shape switch
        {
            "a success without a value" => (DecisionType.Boolean, HttpHarness.Answer(value: null)),
            "a string on a Boolean question" => (DecisionType.Boolean, HttpHarness.Answer(value: marker)),
            "an option the request does not offer" => (DecisionType.Choice, HttpHarness.Answer(type: "\"choice\"", value: marker)),
            "a level the request does not have" => (
                DecisionType.Score,
                HttpHarness.Answer(type: "\"score\"", value: $$"""{"level":{{marker}},"index":0}""")),
            "an index that does not match its level" => (
                DecisionType.Score,
                HttpHarness.Answer(type: "\"score\"", value: """{"level":"high","index":0}""")),
            "an abstain with a value" => (
                DecisionType.Choice,
                HttpHarness.Answer(type: "\"choice\"", outcome: """{"status":"abstain"}""", value: marker, evidence: null)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        harness.Handler.Respond(HttpStatusCode.OK, body);

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(type, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        if (shape == "an abstain with a value")
        {
            result.Outcome.Should().Be(new ProviderOutcome(OutcomeStatus.Abstain));
        }
        else
        {
            result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
            result.Outcome.Kind.Should().Be(FailureKind.Malformed);
        }

        result.Value.Should().BeNull();
        result.ToString().Should().NotContain(ProviderHarness.Marker);
        JsonElement.DeepEquals(result.Raw!.Value, JsonDocument.Parse(body).RootElement).Should().BeTrue();
    }

    public static TheoryData<int, string?, FailureKind> StatusTable => new()
    {
        { 401, null, FailureKind.Unauthorized },
        { 403, null, FailureKind.Unauthorized },
        { 400, null, FailureKind.RejectedInput },
        { 404, null, FailureKind.RejectedInput },
        { 413, null, FailureKind.RejectedInput },
        { 422, null, FailureKind.RejectedInput },
        { 408, null, FailureKind.Unavailable },
        { 429, null, FailureKind.Unavailable },
        { 500, null, FailureKind.Unavailable },
        { 503, null, FailureKind.Unavailable },
        { 504, null, FailureKind.Unavailable },
        { 204, null, FailureKind.Malformed },
        { 302, null, FailureKind.Unknown },
        { 502, HttpHarness.Html("Bad Gateway"), FailureKind.Unavailable },
        { 400, """{"error":{"message":"invalid request","code":"bad_input"}}""", FailureKind.RejectedInput },
        { 500, """{"outcome":{"status":"failure","kind":"unauthorized"}}""", FailureKind.Unavailable },
        { 500, HttpHarness.FailureBody("overloaded", "busy"), FailureKind.Unavailable },
        { 503, HttpHarness.Answer(), FailureKind.Unavailable },
        { 201, HttpHarness.FailureBody("unauthorized", "no key"), FailureKind.Malformed },
    };

    // No body here is a v0 failure body on an error status, so none names the kind: the status decides,
    // and the message says the status and the length only.
    [Theory]
    [MemberData(nameof(StatusTable))]
    public async Task Status_Without_A_Failure_Body_Maps_Through_The_Status_Table(int status, string? body, FailureKind expected)
    {
        HttpHarness harness = new();
        harness.Handler.Respond((HttpStatusCode)status, body, body?.StartsWith('<') == true ? "text/html" : "application/json");

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(expected);
        result.Outcome.Message.Should().Be($"HTTP {status}, body {Encoding.UTF8.GetByteCount(body ?? "")} bytes");
        if (body is null || body.StartsWith('<'))
        {
            result.Raw.Should().BeNull();
        }
        else
        {
            JsonElement.DeepEquals(result.Raw!.Value, JsonDocument.Parse(body).RootElement).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("HttpRequestException", "HttpRequestException: ConnectionError")]
    [InlineData("HttpIOException", "HttpIOException: ResponseEnded")]
    public async Task Transport_Failure_Reads_As_Unavailable_Naming_Only_The_Error(string exception, string message)
    {
        HttpHarness harness = new();
        harness.Handler.Throw(exception == "HttpRequestException"
            ? new HttpRequestException(HttpRequestError.ConnectionError, $"refused while sending {ProviderHarness.Marker}")
            : new HttpIOException(HttpRequestError.ResponseEnded, $"ended while reading {ProviderHarness.Marker}"));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Kind.Should().Be(FailureKind.Unavailable);
        result.Outcome.Message.Should().Be(message);
    }

    [Theory]
    [InlineData("an abstain's message")]
    [InlineData("a failure body's message")]
    [InlineData("an error object on 400")]
    public async Task Server_Text_Never_Reaches_The_Outcome_Message(string text)
    {
        HttpHarness harness = new();
        switch (text)
        {
            case "an abstain's message":
                harness.Handler.Respond(
                    HttpStatusCode.OK,
                    HttpHarness.Answer(outcome: $$"""{"status":"abstain","message":"declined {{ProviderHarness.Marker}}"}""", value: null));
                break;
            case "a failure body's message":
                harness.Handler.Respond(
                    HttpStatusCode.ServiceUnavailable,
                    HttpHarness.FailureBody("unavailable", $"model not loaded for {ProviderHarness.Marker}"));
                break;
            case "an error object on 400":
                harness.Handler.Respond(
                    HttpStatusCode.BadRequest,
                    $$$"""{"error":{"message":"invalid: {{{ProviderHarness.Marker}}}","code":"{{{ProviderHarness.Marker}}}"}}""");
                break;
        }

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        (result.Outcome.Message ?? "").Should().NotContain(ProviderHarness.Marker);
        result.ToString().Should().NotContain(ProviderHarness.Marker);
        result.Raw!.Value.GetRawText().Should().Contain(ProviderHarness.Marker);
    }

    [Theory]
    [InlineData(504, "timeout", FailureKind.Timeout)]
    [InlineData(400, "unauthorized", FailureKind.Unauthorized)]
    [InlineData(500, "rejectedInput", FailureKind.RejectedInput)]
    public async Task Failure_Body_Kind_Wins_Over_The_Status(int status, string kind, FailureKind expected)
    {
        HttpHarness harness = new();
        string body = HttpHarness.FailureBody(kind, $"refused {ProviderHarness.Marker}");
        harness.Handler.Respond((HttpStatusCode)status, body);

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(expected);
        result.Outcome.Message.Should().Be($"HTTP {status}, kind {kind}, body {Encoding.UTF8.GetByteCount(body)} bytes");
        result.Provider.Model.Should().Be(HttpHarness.Model);
        JsonElement.DeepEquals(result.Raw!.Value, JsonDocument.Parse(body).RootElement).Should().BeTrue();
    }

    // A byte order mark in front of the JSON is skipped, so the body reads as it would without one.
    [Fact]
    public async Task Byte_Order_Mark_Before_A_Success_Is_Skipped()
    {
        HttpHarness harness = new();
        harness.Handler.RespondWithByteOrderMark(HttpStatusCode.OK, HttpHarness.Success(DecisionType.Boolean));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().Be(new BooleanValue(true));
    }

    [Fact]
    public async Task Byte_Order_Mark_Before_A_Failure_Body_Is_Skipped()
    {
        HttpHarness harness = new();
        harness.Handler.RespondWithByteOrderMark(
            HttpStatusCode.InternalServerError,
            HttpHarness.FailureBody("rejectedInput", $"refused {ProviderHarness.Marker}"));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.RejectedInput);
    }
}
