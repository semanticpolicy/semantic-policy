using System.Net.Http.Headers;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevRequestTests
{
    public static TheoryData<string, string> Routes => new()
    {
        { "typesafe", "https://api.typesafe.ai/v1/systemone" },
        { "openrouter", "https://openrouter.ai/api/v1/systemone" },
        { "custom", "https://gateway.example/proxy/v1/systemone" },
    };

    public static TheoryData<DecisionType, string> WireTypes => new()
    {
        { DecisionType.Boolean, "noul" },
        { DecisionType.Choice, "choice" },
        { DecisionType.Score, "score" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Request_Targets_The_Route_Url_With_Bearer_And_User_Agent(string preset, string expectedUrl)
    {
        TypeSafeJevHarness harness = new(options => options.Route = RouteFor(preset));
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        RecordedRequest sent = harness.LastRequest;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri!.AbsoluteUri.Should().Be(expectedUrl);
        sent.Header("Authorization").Should().Be($"Bearer {TypeSafeJevHarness.ApiKey}");
        ProductInfoHeaderValue userAgent = ProductInfoHeaderValue.Parse(sent.Header("User-Agent")!);
        userAgent.Product!.Name.Should().Be("SemanticPolicy.Providers.TypeSafe");
        userAgent.Product.Version.Should().NotBeNullOrEmpty().And.NotContain("+");
        sent.Header("Content-Type").Should().Be("application/json");
        harness.Client.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(WireTypes))]
    public async Task Request_Body_Carries_The_Model_The_Context_And_One_Question(DecisionType type, string wireType)
    {
        TypeSafeJevHarness harness = new();
        DecisionRequest request = harness.CreateRequest(type, ProviderHarness.Marker);
        harness.ScriptSuccess(type);

        await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        JsonElement body = harness.LastBody;
        body.EnumerateObject().Select(property => property.Name).Should().Equal("model", "state", "questions");
        body.GetProperty("model").GetString().Should().Be("jev-1.13.0");
        JsonElement.DeepEquals(body.GetProperty("state"), request.Context).Should().BeTrue();
        JsonElement questions = body.GetProperty("questions");
        questions.EnumerateObject().Select(property => property.Name).Should().Equal("decision");
        JsonElement decision = questions.GetProperty("decision");
        decision.GetProperty("type").GetString().Should().Be(wireType);
        decision.GetProperty("instructions").GetString().Should().Be(request.Question);
        JsonElement criteria = decision.GetProperty("criteria");
        switch (type)
        {
            case DecisionType.Boolean:
                criteria.EnumerateObject().Select(property => property.Name).Should().Equal("true", "false");
                criteria.GetProperty("true").GetString().Should().Be(request.Criteria!.True);
                criteria.GetProperty("false").GetString().Should().Be(request.Criteria.False);
                break;
            case DecisionType.Choice:
                JsonElement.DeepEquals(criteria, JsonSerializer.SerializeToElement(request.Options)).Should().BeTrue();
                break;
            case DecisionType.Score:
                criteria.EnumerateArray().Select(level => level.GetString()).Should().Equal(request.Levels);
                break;
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("it names a person", null)]
    [InlineData(null, "it names nobody")]
    public async Task Boolean_Criteria_Follow_The_Sides_Present(string? trueSide, string? falseSide)
    {
        TypeSafeJevHarness harness = new();
        DecisionRequest request = new(
            DecisionType.Boolean,
            "Does the text name a person?",
            JsonSerializer.SerializeToElement("a synthetic line of text"),
            new BooleanCriteria(trueSide, falseSide));
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        JsonElement decision = harness.LastBody.GetProperty("questions").GetProperty("decision");
        if (trueSide is null && falseSide is null)
        {
            decision.TryGetProperty("criteria", out _).Should().BeFalse();
            return;
        }

        JsonElement criteria = decision.GetProperty("criteria");
        criteria.EnumerateObject().Select(property => property.Name).Should().Equal(trueSide is null ? "false" : "true");
        criteria.EnumerateObject().Single().Value.GetString().Should().Be(trueSide ?? falseSide);
    }

    [Theory]
    [InlineData(null, "jev-1.13.0")]
    [InlineData("jev-preview", "jev-preview")]
    public async Task Configured_Model_Overrides_The_Route_Model(string? configured, string expected)
    {
        TypeSafeJevHarness harness = new(options => options.Model = configured);
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        harness.LastBody.GetProperty("model").GetString().Should().Be(expected);
    }

    private static TypeSafeJevRoute RouteFor(string preset) =>
        preset switch
        {
            "typesafe" => TypeSafeJevRoute.TypeSafe,
            "openrouter" => TypeSafeJevRoute.OpenRouter,
            "custom" => new TypeSafeJevRoute(
                new Uri("https://gateway.example/proxy/"),
                "/v1/systemone",
                "jev-custom",
                "GATEWAY_API_KEY"),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
}
