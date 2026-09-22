using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevOptionsTests
{
    [Theory]
    [InlineData("route absent", "Route")]
    [InlineData("timeout zero", "Timeout")]
    [InlineData("timeout negative", "Timeout")]
    [InlineData("model empty over a preset", "Model")]
    [InlineData("relative base url", "BaseUrl")]
    [InlineData("http to a remote host", "BaseUrl")]
    [InlineData("path without a leading slash", "Path")]
    [InlineData("blank id", "Id")]
    public void EnsureValid_Rejects_An_Invalid_Option(string defect, string property)
    {
        TypeSafeJevOptions options = Valid();
        switch (defect)
        {
            case "route absent":
                options.Route = null;
                break;
            case "timeout zero":
                options.Timeout = TimeSpan.Zero;
                break;
            case "timeout negative":
                options.Timeout = TimeSpan.FromSeconds(-1);
                break;
            case "model empty over a preset":
                options.Model = "";
                break;
            case "relative base url":
                options.Route = TypeSafeJevRoute.TypeSafe with { BaseUrl = new Uri("v1", UriKind.Relative) };
                break;
            case "http to a remote host":
                options.Route = TypeSafeJevRoute.TypeSafe with { BaseUrl = new Uri("http://api.example") };
                break;
            case "path without a leading slash":
                options.Route = TypeSafeJevRoute.TypeSafe with { Path = "v1/systemone" };
                break;
            case "blank id":
                options.Id = " ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Action act = options.EnsureValid;

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(property);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    public void EnsureValid_Accepts_Http_On_Loopback(string baseUrl)
    {
        TypeSafeJevOptions options = Valid();
        options.Route = TypeSafeJevRoute.TypeSafe with { BaseUrl = new Uri(baseUrl) };

        Action act = options.EnsureValid;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Provider_Constructor_Requires_An_Api_Key_And_Snapshots_The_Options()
    {
        TypeSafeJevOptions withoutKey = Valid();
        withoutKey.ApiKey = null;
        using HttpClient client = new(new ScriptedHttpMessageHandler());

        Action construct = () => new TypeSafeJevProvider(client, withoutKey);

        construct.Should().Throw<ArgumentException>().Which.Message.Should().Contain("ApiKey");

        TypeSafeJevHarness harness = new(options => options.Model = "jev-pinned");
        harness.JevOptions.Model = "jev-other";
        harness.JevOptions.Id = "other-id";
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        harness.LastBody.GetProperty("model").GetString().Should().Be("jev-pinned");
        harness.Provider.Id.Should().Be("typesafe-jev");
    }

    private static TypeSafeJevOptions Valid() =>
        new() { Route = TypeSafeJevRoute.TypeSafe, ApiKey = TypeSafeJevHarness.ApiKey };
}
