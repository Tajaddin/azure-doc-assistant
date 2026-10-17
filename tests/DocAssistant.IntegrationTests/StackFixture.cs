using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Ingestion;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace DocAssistant.IntegrationTests;

/// <summary>Real Postgres (pgvector) and Azurite in containers, shared by the whole test collection.</summary>
public sealed class StackFixture : IAsyncLifetime
{
    // Azurite's well-known development account; it only ever listens on the test host.
    private const string AzuriteAccount = "devstoreaccount1";
    private const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("docs").WithUsername("docs").WithPassword("docs")
        .Build();

    // A plain container rather than the Azurite module so --skipApiVersionCheck can be passed: the Azure SDKs
    // adopt new service API versions faster than Azurite releases support them.
    public IContainer Azurite { get; } = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0")
        .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck", "--location", "/data")
        .WithPortBinding(10000, true)
        .WithPortBinding(10001, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Azurite Queue service is successfully listening"))
        .Build();

    public string AzuriteConnectionString =>
        $"DefaultEndpointsProtocol=http;AccountName={AzuriteAccount};AccountKey={AzuriteKey};" +
        $"BlobEndpoint=http://{Azurite.Hostname}:{Azurite.GetMappedPublicPort(10000)}/{AzuriteAccount};" +
        $"QueueEndpoint=http://{Azurite.Hostname}:{Azurite.GetMappedPublicPort(10001)}/{AzuriteAccount};";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), Azurite.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Azurite.DisposeAsync();
    }
}

[CollectionDefinition("stack")]
public sealed class StackCollection : ICollectionFixture<StackFixture>;

public sealed class ApiFactory(StackFixture stack, IDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "fake",
            ["Postgres:ConnectionString"] = stack.Postgres.GetConnectionString(),
            ["Storage:ConnectionString"] = stack.AzuriteConnectionString,
            ["Auth:Mode"] = "dev",
            ["Auth:Dev:SigningKey"] = SigningKey,
            ["RateLimits:AskPerMinute"] = "1000",
            ["RateLimits:UploadPerMinute"] = "1000",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                settings[k] = v;
            }
        }
        // UseSetting feeds the WebApplicationBuilder's initial configuration, which Program.cs reads at start-up;
        // ConfigureAppConfiguration would only become visible after the builder has already bound its options.
        foreach (var (k, v) in settings)
        {
            builder.UseSetting(k, v);
        }
    }

    /// <summary>Plays the worker: drains the ingestion queue through the same pipeline the worker hosts.</summary>
    public async Task<int> DrainIngestionQueueAsync(CancellationToken ct = default)
    {
        var queue = Services.GetRequiredService<IIngestionQueue>();
        var pipeline = Services.GetRequiredService<IngestionPipeline>();
        var processed = 0;
        while (await queue.DequeueAsync(ct) is { } item)
        {
            try
            {
                await pipeline.RunAsync(item.Message, ct);
            }
            catch (Exception)
            {
                // the pipeline already marked the document Failed; the message is dropped here for determinism
            }
            await queue.CompleteAsync(item, ct);
            processed++;
        }
        return processed;
    }
}
