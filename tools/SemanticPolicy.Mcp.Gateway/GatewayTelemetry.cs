using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SemanticPolicy.Telemetry;

namespace SemanticPolicy.Mcp.Gateway;

// The OTLP export of the evaluator's own spans and metrics, built only for a signal the environment names an endpoint
// for. It exports the evaluator's activity source and meter and nothing else: not the MCP SDK's, whose spans describe
// the upstream server and its child process. Disposing it flushes what is still batched.
internal sealed class GatewayTelemetry : IDisposable
{
    private const string _tracesSignal = "TRACES";
    private const string _metricsSignal = "METRICS";

    // What a signal's own variables set besides its endpoint, each in place of the generic variable for the same setting.
    private static readonly string[] _settings = ["PROTOCOL", "HEADERS", "TIMEOUT", "COMPRESSION"];

    private readonly TracerProvider? _traces;
    private readonly MeterProvider? _metrics;

    private GatewayTelemetry(TracerProvider? traces, MeterProvider? metrics)
    {
        _traces = traces;
        _metrics = metrics;
    }

    // Which signals a provider was built for. The gateway never reads these; its tests do.
    public bool ExportsTraces => _traces is not null;

    public bool ExportsMetrics => _metrics is not null;

    // Null when the environment names no endpoint. The exporter reads its OTEL_ variables from the environment the
    // gateway was given, never the process's. The SDK registers a configuration over the process's environment before
    // ConfigureServices runs; the one registered here comes later, and the container resolves the last registration.
    public static GatewayTelemetry? Start(IDictionary environment)
    {
        Dictionary<string, string?> variables = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string name && name.StartsWith("OTEL_", StringComparison.Ordinal))
            {
                variables[name] = entry.Value as string;
            }
        }

        bool traces = Named(variables, Variable(null, "ENDPOINT")) || Named(variables, Variable(_tracesSignal, "ENDPOINT"));
        bool metrics = Named(variables, Variable(null, "ENDPOINT")) || Named(variables, Variable(_metricsSignal, "ENDPOINT"));
        if (!traces && !metrics)
        {
            return null;
        }

        return new GatewayTelemetry(
            traces
                ? Sdk.CreateTracerProviderBuilder()
                    .ConfigureServices(services => services.AddSingleton(Configuration(variables, _tracesSignal)))
                    .AddSource(SemanticPolicyTelemetry.ActivitySourceName)
                    .AddOtlpExporter(options => SetEndpoint(options, variables, _tracesSignal))
                    .Build()
                : null,
            metrics
                ? Sdk.CreateMeterProviderBuilder()
                    .ConfigureServices(services => services.AddSingleton(Configuration(variables, _metricsSignal)))
                    .AddMeter(SemanticPolicyTelemetry.MeterName)
                    .AddOtlpExporter(options => SetEndpoint(options, variables, _metricsSignal))
                    .Build()
                : null);
    }

    public void Dispose()
    {
        _traces?.Dispose();
        _metrics?.Dispose();
    }

    // AddOtlpExporter reads only the generic variables, so in the configuration a signal's provider is given, that
    // signal's own variables stand in for them.
    private static IConfiguration Configuration(Dictionary<string, string?> variables, string signal)
    {
        Dictionary<string, string?> configured = new(variables, StringComparer.Ordinal);
        foreach (string setting in _settings)
        {
            if (Named(variables, Variable(signal, setting)))
            {
                configured[Variable(null, setting)] = variables[Variable(signal, setting)];
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(configured).Build();
    }

    // A signal's own endpoint is set on the options rather than in the configuration: one read from the generic variable
    // has the signal's path appended under http/protobuf, while a signal's own is used as it is given. One that is not
    // an absolute URI is ignored, as the exporter ignores a generic one.
    private static void SetEndpoint(OtlpExporterOptions options, Dictionary<string, string?> variables, string signal)
    {
        if (Uri.TryCreate(variables.GetValueOrDefault(Variable(signal, "ENDPOINT")), UriKind.Absolute, out Uri? endpoint))
        {
            options.Endpoint = endpoint;
        }
    }

    // OTEL_EXPORTER_OTLP_{setting}, or OTEL_EXPORTER_OTLP_{signal}_{setting} for a signal's own.
    private static string Variable(string? signal, string setting) =>
        signal is null ? $"OTEL_EXPORTER_OTLP_{setting}" : $"OTEL_EXPORTER_OTLP_{signal}_{setting}";

    private static bool Named(Dictionary<string, string?> variables, string name) =>
        !string.IsNullOrWhiteSpace(variables.GetValueOrDefault(name));
}
