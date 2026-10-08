using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// Everything the gateway file configures, read, checked and built: each point's policy and mapping, and an evaluator
/// over every provider those policies bind. A composition exists only for a sound configuration, so nothing is served
/// before every file has been read and every bound provider built.
/// </summary>
public sealed class GatewayComposition
{
    // A call is cut at the budget of the policy that makes it, which the policy file sets; each adapter keeps its own
    // default timer behind that.
    private const string _timeoutRefusal =
        "a call is limited by the budget of the policy that makes it, so set budget in the policy file";

    private GatewayComposition(
        IPolicyEvaluator evaluator,
        GatewayPoint? results,
        GatewayPoint? definitions,
        IReadOnlyList<string> keyVariables)
    {
        Evaluator = evaluator;
        Results = results;
        Definitions = definitions;
        KeyVariables = keyVariables;
    }

    /// <summary>
    /// Evaluates both points' policies. It holds those policies and the providers they bind, every one already built.
    /// </summary>
    public IPolicyEvaluator Evaluator { get; }

    /// <summary>The point that screens tool results, or <see langword="null"/> when the gateway file has none.</summary>
    public GatewayPoint? Results { get; }

    /// <summary>The point that screens tool definitions, or <see langword="null"/> when the gateway file has none.</summary>
    public GatewayPoint? Definitions { get; }

    /// <summary>
    /// Every environment variable the providers file names as holding a key, bound or not, each once, in the order the
    /// file names them.
    /// </summary>
    public IReadOnlyList<string> KeyVariables { get; }

    /// <summary>
    /// Reads the gateway file and every file it names, and builds the providers its policies bind and no other, so an
    /// entry the policies leave unbound cannot stop the gateway with a missing key.
    /// </summary>
    /// <param name="path">The gateway file. The paths inside it are resolved against its directory.</param>
    /// <exception cref="GatewayException">
    /// A file cannot be read or is wrong, a policy binds a provider the providers file does not register or cannot
    /// answer, or a bound provider cannot be built, such as one whose key variable is unset. The message names the file
    /// and the point, key, policy or provider at fault, never a value from a file.
    /// </exception>
    public static GatewayComposition Compose(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        GatewayFileContent file = GatewayFile.Read(path);
        Loaded? results = file.Results is { } resultsEntry ? Load(path, resultsEntry) : null;
        Loaded? definitions = file.Definitions is { } definitionsEntry ? Load(path, definitionsEntry) : null;
        Loaded[] points = [.. new[] { results, definitions }.OfType<Loaded>()];
        if (file.ProvidersPath is not { } providersPath)
        {
            return new GatewayComposition(new PolicyEvaluator([], []), null, null, []);
        }

        ProviderRegistrations registrations;
        try
        {
            registrations = ProvidersFileReader.Read(providersPath, timeout: null, _timeoutRefusal);
        }
        catch (ProvidersFileException refusal)
        {
            throw new GatewayException(refusal.Message);
        }

        IReadOnlyList<ProviderRegistration> providers = Build(path, providersPath, registrations, points);
        return new GatewayComposition(
            Evaluate(path, providers, points),
            results?.Point,
            definitions?.Point,
            registrations.KeyVariables);
    }

    // Read with Core's own JSON settings and held to the validation Core applies at first use.
    private static Loaded Load(string gatewayPath, PointEntry entry)
    {
        string where = $"Gateway file '{gatewayPath}': {entry.Name}.policy '{entry.PolicyPath}'";
        string json;
        try
        {
            json = File.ReadAllText(entry.PolicyPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GatewayException($"{where} cannot be read: {e.Message}");
        }

        Policy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<Policy>(json, SemanticPolicyJson.Options);
        }
        catch (JsonException e)
        {
            // Never the exception's message: it can quote the text around the error, such as a rule's question.
            throw new GatewayException($"{where} is not Core's policy JSON at {Position(e)}.");
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            // A record constructor refusing a missing member throws an ArgumentException, and a shape the format does
            // not take a NotSupportedException; neither carries a position.
            string detail = e is ArgumentException { ParamName: { } member }
                ? $"'{member}' is missing or not valid"
                : "it holds a shape the policy format does not take";
            throw new GatewayException($"{where} is not Core's policy JSON: {detail}.");
        }

        if (policy is null)
        {
            throw new GatewayException($"{where} holds no policy.");
        }

        try
        {
            policy.Validate();
        }
        catch (PolicyConfigurationException e)
        {
            // Core's message names the policy, rule and provider ids and never the policy's text.
            throw new GatewayException($"{where}: {e.Message}");
        }

        return new Loaded(entry, new GatewayPoint(entry.Name, policy, entry.Mapping));
    }

    // Each bound adapter is built here, at start, because building is where an adapter reads its key variable: left
    // to the first call, an unset key would surface on the first tool result instead of stopping the gateway.
    private static List<ProviderRegistration> Build(
        string gatewayPath,
        string providersPath,
        ProviderRegistrations registrations,
        IEnumerable<Loaded> points)
    {
        ServiceCollection services = new();
        CapturingBuilder builder = new(services.AddSemanticPolicy());
        try
        {
            registrations.Register(builder);
        }
        catch (ProvidersFileException refusal)
        {
            throw new GatewayException(refusal.Message);
        }

        // The providers file refuses a name given twice, so each name is captured once.
        Dictionary<string, Func<IServiceProvider, IDecisionProvider>> factories = builder.Captured.ToDictionary(
            captured => captured.Name,
            captured => captured.Factory,
            StringComparer.Ordinal);
        List<string> bound = [];
        foreach ((PointEntry entry, GatewayPoint point) in points)
        {
            foreach (ProviderBinding binding in point.Policy.Bindings)
            {
                if (!factories.ContainsKey(binding.ProviderId))
                {
                    string registered = factories.Count == 0
                        ? "none"
                        : string.Join(", ", factories.Keys.Order(StringComparer.Ordinal));
                    throw new GatewayException(
                        $"Gateway file '{gatewayPath}': {entry.Name}.policy '{entry.PolicyPath}': policy '{point.Policy.Id}' binds provider '{binding.ProviderId}', which providers file '{providersPath}' does not register; it registers {registered}.");
                }

                if (!bound.Contains(binding.ProviderId, StringComparer.Ordinal))
                {
                    bound.Add(binding.ProviderId);
                }
            }
        }

        // Not disposed: the gateway calls the adapters built over it until the process ends.
        ServiceProvider container = services.BuildServiceProvider();
        List<ProviderRegistration> providers = [];
        foreach (string name in bound)
        {
            try
            {
                providers.Add(new ProviderRegistration(name, factories[name](container)));
            }
            catch (PolicyConfigurationException failure)
            {
                // The adapter's message names the provider and the variable, never the key.
                throw new GatewayException($"Providers file '{providersPath}': {failure.Message}");
            }
        }

        return providers;
    }

    // Core's evaluator checks each policy against the providers it binds as it is constructed: whether every provider
    // answers the rules' decision types and produces the evidence the thresholds read.
    private static PolicyEvaluator Evaluate(string gatewayPath, IReadOnlyList<ProviderRegistration> providers, Loaded[] points)
    {
        // One policy file may serve both points. Two files that share an id would leave the evaluator, and a trace that
        // names the policy, unable to tell them apart.
        if (points is [var first, var second]
            && first.Point.Policy.Id == second.Point.Policy.Id
            && !PathComparer.Equals(first.Entry.PolicyPath, second.Entry.PolicyPath))
        {
            throw new GatewayException(
                $"Gateway file '{gatewayPath}': {second.Entry.Name}.policy '{second.Entry.PolicyPath}' holds policy '{second.Point.Policy.Id}', as {first.Entry.Name}.policy '{first.Entry.PolicyPath}' does; give each policy its own id.");
        }

        try
        {
            return new PolicyEvaluator(providers, points.DistinctBy(loaded => loaded.Entry.PolicyPath, PathComparer).Select(loaded => loaded.Point.Policy));
        }
        catch (PolicyConfigurationException failure)
        {
            string at = points.FirstOrDefault(loaded => loaded.Point.Policy.Id == failure.PolicyId) is { } loaded
                ? $": {loaded.Entry.Name}.policy '{loaded.Entry.PolicyPath}'"
                : string.Empty;
            throw new GatewayException($"Gateway file '{gatewayPath}'{at}: {failure.Message}");
        }
    }

    private static string Position(JsonException e)
    {
        string position = string.Create(
            CultureInfo.InvariantCulture,
            $"line {e.LineNumber + 1 ?? 1}, byte {e.BytePositionInLine + 1 ?? 1}");
        return e.Path is null or "$" ? position : $"{position}, in {e.Path}";
    }

    // Windows filesystems treat two spellings of one path as the same file, and GetFullPath keeps the spelling given.
    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Loaded(PointEntry Entry, GatewayPoint Point);

    // Core's ProviderRegistration is built with its provider, so reading the names back from the container would
    // build every adapter and fail on any one that cannot be built. The names and factories are captured here, on
    // the way in, and every call is still forwarded, so Core validates the arguments as it would for an application.
    private sealed class CapturingBuilder(ISemanticPolicyBuilder inner) : ISemanticPolicyBuilder
    {
        public List<(string Name, Func<IServiceProvider, IDecisionProvider> Factory)> Captured { get; } = [];

        public IServiceCollection Services => inner.Services;

        public ISemanticPolicyBuilder AddProvider(string name, Func<IServiceProvider, IDecisionProvider> factory)
        {
            inner.AddProvider(name, factory);
            Captured.Add((name, factory));
            return this;
        }

        public ISemanticPolicyBuilder AddProvider(IDecisionProvider provider, string? name = null)
        {
            inner.AddProvider(provider, name);
            Captured.Add((name ?? provider.Id, _ => provider));
            return this;
        }

        public ISemanticPolicyBuilder AddPolicy(Policy policy)
        {
            inner.AddPolicy(policy);
            return this;
        }
    }
}
