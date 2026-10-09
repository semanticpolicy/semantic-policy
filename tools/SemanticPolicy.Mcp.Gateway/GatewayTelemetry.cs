using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SemanticPolicy.Telemetry;

namespace SemanticPolicy.Mcp.Gateway;

// The OTLP export of the evaluator's own spans and metrics, built only for a signal the environment names an endpoint
// for. It exports the evaluator's activity source and meter and nothing else: not the MCP SDK's, whose spans describe
// the upstream server and its child process. Disposing it flushes what is still batched.
internal sealed class GatewayTelemetry : IDisposable
{
    private const string _endpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string _tracesEndpoint = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    private const string _metricsEndpoint = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";

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

        bool traces = Named(variables, _endpoint) || Named(variables, _tracesEndpoint);
        bool metrics = Named(variables, _endpoint) || Named(variables, _metricsEndpoint);
        if (!traces && !metrics)
        {
            return null;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(variables).Build();
        return new GatewayTelemetry(
            traces
                ? Sdk.CreateTracerProviderBuilder()
                    .ConfigureServices(services => services.AddSingleton(configuration))
                    .AddSource(SemanticPolicyTelemetry.ActivitySourceName)
                    .AddOtlpExporter()
                    .Build()
                : null,
            metrics
                ? Sdk.CreateMeterProviderBuilder()
                    .ConfigureServices(services => services.AddSingleton(configuration))
                    .AddMeter(SemanticPolicyTelemetry.MeterName)
                    .AddOtlpExporter()
                    .Build()
                : null);
    }

    public void Dispose()
    {
        _traces?.Dispose();
        _metrics?.Dispose();
    }

    private static bool Named(Dictionary<string, string?> variables, string name) =>
        !string.IsNullOrWhiteSpace(variables.GetValueOrDefault(name));
}
