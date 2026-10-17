using DocAssistant.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Azure Functions isolated worker host. Configuration comes from app settings (Ai__Provider and friends),
// which Bicep wires to Key Vault references; locally local.settings.json (gitignored) holds them.
var builder = FunctionsApplication.CreateBuilder(args);
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();
builder.Services.AddDocAssistant(builder.Configuration);

var host = builder.Build();
await host.Services.InitializeDocAssistantAsync(CancellationToken.None);
host.Run();
