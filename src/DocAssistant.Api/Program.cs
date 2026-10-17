using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using DocAssistant.Api.Auth;
using DocAssistant.Api.Endpoints;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- logging: JSON lines to stdout
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// ---------------------------------------------------------------- configuration sources
var keyVaultUri = config["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    config.AddAzureKeyVault(new Uri(keyVaultUri), new Azure.Identity.DefaultAzureCredential());
}

// ---------------------------------------------------------------- services
builder.Services.AddDocAssistant(config);
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "DocAssistant API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    });
    o.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); // "Ready", not 2
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

// ---------------------------------------------------------------- auth: Entra ID or dev tokens
var auth = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(auth);
if (auth.Mode == "entra")
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(config.GetSection("AzureAd"));
}
else
{
    var issuer = new DevTokenIssuer(auth.Dev);
    builder.Services.AddSingleton(issuer);
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth.Dev.Issuer,
            ValidAudience = auth.Dev.Audience,
            IssuerSigningKey = issuer.Key,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
        o.MapInboundClaims = false;
    });
}
builder.Services.AddAuthorization();

// ---------------------------------------------------------------- rate limiting, per user (per IP before auth)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.IsAuthenticated == true ? ctx.User.OwnerId() : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimits:AskPerMinute", 30), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("upload", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.IsAuthenticated == true ? ctx.User.OwnerId() : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimits:UploadPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("token", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---------------------------------------------------------------- CORS for the static web app origin
var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

// ---------------------------------------------------------------- telemetry: OTLP locally, Application Insights in Azure
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(config["OTEL_SERVICE_NAME"] ?? "docassistant-api", serviceVersion: "0.1.0"))
    .WithTracing(t => t
        .AddSource(AskService.Activity.Name)
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/healthz") && !ctx.Request.Path.StartsWithSegments("/readyz") && !ctx.Request.Path.StartsWithSegments("/metrics"))
        .AddHttpClientInstrumentation()
        .AddNpgsql())
    .WithMetrics(m => m
        .AddMeter(AskService.Meter.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());
if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}
if (!string.IsNullOrWhiteSpace(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    otel.UseAzureMonitor();
}

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "internal error", traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier });
}));
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "DocAssistant v1"));
app.MapPrometheusScrapingEndpoint("/metrics");

app.MapHealth();
app.MapAuth(auth);
app.MapDocuments();
app.MapAsk();
app.MapCosts();

// static web assets when the React build is copied into wwwroot (compose); the SWA serves them in Azure
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

await app.Services.InitializeDocAssistantAsync(CancellationToken.None);
app.Logger.LogInformation("startup provider={Provider} analyzer={Analyzer} auth={Auth}",
    config["Ai:Provider"] ?? "ollama", config["Analyzer:Provider"] ?? "pdfpig", auth.Mode);
app.Run();

public partial class Program;
