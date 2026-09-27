using SemanticPolicy.Providers.ContractTests.Contract;

namespace SemanticPolicy.Providers.ContractTests.Http;

public sealed class HttpContractTests : ProviderContractTests
{
    protected override ProviderHarness CreateHarness() => new HttpHarness();
}
