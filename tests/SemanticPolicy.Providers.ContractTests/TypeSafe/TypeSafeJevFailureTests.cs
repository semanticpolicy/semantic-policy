using System.Net;
using System.Text;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevFailureTests
{
    public static TheoryData<string, int, string, string, string> VendorErrors => new()
    {
        {
            "an OpenRouter error with a code",
            400,
            JevFixtures.OpenRouterError(400, $"the state is invalid near {ProviderHarness.Marker}"),
            "application/json",
            "HTTP 400, code 400"
        },
        {
            "a TypeSafe error with an error type",
            401,
            JevFixtures.TypeSafeError("authentication_error", $"no key for {ProviderHarness.Marker}"),
            "application/json",
            "HTTP 401, error_type authentication_error"
        },
        {
            "a gateway's HTML page",
            503,
            JevFixtures.Html(ProviderHarness.Marker),
            "text/html",
            "HTTP 503"
        },
    };

    [Theory]
    [InlineData(401, FailureKind.Unauthorized)]
    [InlineData(403, FailureKind.Unauthorized)]
    [InlineData(400, FailureKind.RejectedInput)]
    [InlineData(404, FailureKind.RejectedInput)]
    [InlineData(413, FailureKind.RejectedInput)]
    [InlineData(422, FailureKind.RejectedInput)]
    [InlineData(408, FailureKind.Unavailable)]
    [InlineData(429, FailureKind.Unavailable)]
    [InlineData(500, FailureKind.Unavailable)]
    [InlineData(503, FailureKind.Unavailable)]
    [InlineData(529, FailureKind.Unavailable)]
    [InlineData(302, FailureKind.Unknown)]
    [InlineData(405, FailureKind.Unknown)]
    [InlineData(418, FailureKind.Unknown)]
    [InlineData(201, FailureKind.Malformed)]
    [InlineData(204, FailureKind.Malformed)]
    public async Task Http_Status_Maps_To_The_Failure_Kind_Of_The_Table(int status, FailureKind kind)
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond((HttpStatusCode)status, status == 204 ? null : "{}");

        ProviderResult result = await Decide(harness);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(kind);
    }

    [Fact]
    public async Task Connection_Failure_Is_Unavailable_Naming_The_Error_Kind()
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Throw(
            new HttpRequestException(HttpRequestError.ConnectionError, $"refused while sending {ProviderHarness.Marker}"));

        ProviderResult result = await Decide(harness);

        result.Outcome.Kind.Should().Be(FailureKind.Unavailable);
        result.Outcome.Message.Should().Be("HttpRequestException: ConnectionError");
        result.Outcome.Message.Should().NotContain(ProviderHarness.Marker);
    }

    [Theory]
    [MemberData(nameof(VendorErrors))]
    public async Task Failure_Message_Names_Status_Vendor_Code_And_Body_Length_Never_Vendor_Text(
        string bodyKind,
        int status,
        string body,
        string mediaType,
        string expectedPrefix)
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond((HttpStatusCode)status, body, mediaType);

        ProviderResult result = await Decide(harness);

        result.Outcome.Message.Should().Be(
            $"{expectedPrefix}, body {Encoding.UTF8.GetByteCount(body)} bytes",
            "{0} is described by status, vendor code and length alone",
            bodyKind);
        result.Outcome.Message.Should().NotContain(ProviderHarness.Marker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_Raw_Is_The_Parsed_Json_Body_Or_Null(bool json)
    {
        TypeSafeJevHarness harness = new();
        string body = json
            ? JevFixtures.OpenRouterError(429, $"slow down {ProviderHarness.Marker}")
            : JevFixtures.Html(ProviderHarness.Marker);
        harness.Handler.Respond(HttpStatusCode.TooManyRequests, body, json ? "application/json" : "text/html");

        ProviderResult result = await Decide(harness);

        if (json)
        {
            result.Raw.Should().NotBeNull();
            JsonElement.DeepEquals(result.Raw!.Value, JsonSerializer.Deserialize<JsonElement>(body)).Should().BeTrue();
        }
        else
        {
            result.Raw.Should().BeNull();
        }
    }

    [Fact]
    public async Task Adapter_Timeout_Yields_A_Timeout_Failure_With_An_Uncancelled_Caller_Token()
    {
        TypeSafeJevHarness harness = new(options => options.Timeout = TimeSpan.FromMilliseconds(200));
        harness.Handler.Hang();
        using CancellationTokenSource caller = new();

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            caller.Token);

        result.Outcome.Should().Be(ProviderOutcome.Failure(FailureKind.Timeout, "no response within 200 ms"));
        result.Provider.LatencyMs.Should().BeGreaterThanOrEqualTo(200).And.BeLessThan(5000);
        caller.Token.IsCancellationRequested.Should().BeFalse();
        harness.LastRequest.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Adapter_Timeout_Covers_Reading_The_Body()
    {
        TypeSafeJevHarness harness = new(options => options.Timeout = TimeSpan.FromMilliseconds(200));
        harness.Handler.HangBody(HttpStatusCode.OK);

        ProviderResult result = await Decide(harness);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        result.Outcome.Kind.Should().Be(FailureKind.Timeout);
    }

    private static Task<ProviderResult> Decide(TypeSafeJevHarness harness) =>
        harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);
}
