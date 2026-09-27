using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;

namespace SemanticPolicy.Providers.ContractTests.Http;

public sealed class HttpProviderTransportTests : HttpTransportTests
{
    protected override void Register(ISemanticPolicyBuilder builder, string name) =>
        builder.AddHttpProvider(name, options =>
        {
            options.BaseUrl = HttpHarness.BaseUrl;
            options.Model = HttpHarness.Model;
            options.Types = [DecisionType.Boolean];
            options.Evidence = [EvidenceKind.Score];
            options.StructuredContext = false;
        });

    private protected override (ProviderHarness Harness, ScriptedHttpMessageHandler Handler) CreateTransport()
    {
        HttpHarness harness = new();
        return (harness, harness.Handler);
    }
}
