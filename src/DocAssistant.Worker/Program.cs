using Azure.Monitor.OpenTelemetry.Exporter;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure;
using DocAssistant.Worker;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The ingestion worker is a BackgroundService hosted next to a minimal Kestrel endpoint that serves
// /healthz and Prometheus /metrics. Kestrel binds to every interface, which HttpListener cannot do portably.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new Azure.Identity.DefaultAzureCredential());
}

builder.Services.AddDocAssistant(builder.Configuration);
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "docassistant-worker", serviceVersion: "0.1.0"))
    .WithTracing(t => t.AddSource(AskService.Activity.Name).AddSource(IngestionWorker.Activity.Name).AddHttpClientInstrumentation().AddNpgsql())
    .WithMetrics(m => m
        .AddMeter(AskService.Meter.Name).AddMeter(IngestionWorker.Meter.Name)
        .AddHttpClientInstrumentation().AddRuntimeInstrumentation()
        .AddPrometheusExporter());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    otel.UseAzureMonitorExporter();
}

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapPrometheusScrapingEndpoint("/metrics");
await app.Services.InitializeDocAssistantAsync(CancellationToken.None);
app.Run();
