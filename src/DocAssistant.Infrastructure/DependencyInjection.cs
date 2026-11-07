using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Extraction;
using DocAssistant.Core.Ingestion;
using DocAssistant.Core.Pricing;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure.Ai;
using DocAssistant.Infrastructure.Analysis;
using DocAssistant.Infrastructure.Postgres;
using DocAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace DocAssistant.Infrastructure;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Azure Storage connection string; "UseDevelopmentStorage=true" targets Azurite. Ignored when the service URIs are set.</summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";
    /// <summary>Blob endpoint (https://account.blob.core.windows.net/). With QueueServiceUri, storage is reached with the managed identity.</summary>
    public string BlobServiceUri { get; set; } = "";
    public string QueueServiceUri { get; set; } = "";
    public string Container { get; set; } = "documents";
    public string Queue { get; set; } = "ingest";
}

public sealed class PostgresOptions
{
    public const string Section = "Postgres";
    public string ConnectionString { get; set; } = "Host=localhost;Port=5434;Username=docs;Password=docs;Database=docs";
}

public sealed class AnalyzerOptions
{
    public const string Section = "Analyzer";
    /// <summary>pdfpig (local) or azure (Document Intelligence; needs Endpoint and either ApiKey or a managed identity).</summary>
    public string Provider { get; set; } = "pdfpig";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public static class DependencyInjection
{
    /// <summary>Everything the API, the worker, the Function and the eval runner share.</summary>
    public static IServiceCollection AddDocAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        var ai = configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        var postgres = configuration.GetSection(PostgresOptions.Section).Get<PostgresOptions>() ?? new PostgresOptions();
        var analyzer = configuration.GetSection(AnalyzerOptions.Section).Get<AnalyzerOptions>() ?? new AnalyzerOptions();
        services.AddSingleton(ai);
        services.AddSingleton(storage);
        services.AddSingleton(postgres);
        services.AddSingleton(analyzer);

        var pricingOverrides = configuration.GetSection("Pricing:Models").GetChildren()
            .ToDictionary(c => c.Key, c => (c.GetValue<decimal>("Input"), c.GetValue<decimal>("Output")));
        services.AddSingleton(new ModelPricing(pricingOverrides));

        services.AddSingleton(sp => KernelFactory.Create(ai, sp));
        services.AddSingleton<IEmbedder>(sp => KernelFactory.CreateEmbedder(sp.GetRequiredService<Kernel>(), ai));

        services.AddSingleton(new Db(postgres.ConnectionString, ai.EmbeddingDimensions));
        services.AddSingleton<IDocumentRepository, PostgresDocumentRepository>();
        services.AddSingleton<IDocumentIndex, PgVectorDocumentIndex>();
        services.AddSingleton<ICostLedger, PostgresCostLedger>();

        var (blobs, queues) = CreateStorageClients(storage);
        services.AddSingleton(blobs);
        services.AddSingleton(queues);
        services.AddSingleton<BlobDocumentStore>(sp => new BlobDocumentStore(sp.GetRequiredService<BlobServiceClient>(), storage.Container));
        services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<BlobDocumentStore>());
        services.AddSingleton<QueueIngestionQueue>(sp => new QueueIngestionQueue(sp.GetRequiredService<QueueServiceClient>(), storage.Queue));
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<QueueIngestionQueue>());

        if (analyzer.Provider == "azure")
        {
            services.AddSingleton<IDocumentAnalyzer>(_ =>
            {
                var endpoint = new Uri(analyzer.Endpoint);
                var client = string.IsNullOrWhiteSpace(analyzer.ApiKey)
                    ? new Azure.AI.DocumentIntelligence.DocumentIntelligenceClient(endpoint, new Azure.Identity.DefaultAzureCredential())
                    : new Azure.AI.DocumentIntelligence.DocumentIntelligenceClient(endpoint, new Azure.AzureKeyCredential(analyzer.ApiKey));
                return new DocumentIntelligenceAnalyzer(client);
            });
        }
        else
        {
            services.AddSingleton<IDocumentAnalyzer, PdfPigAnalyzer>();
        }

        services.AddSingleton<TextChunker>();
        services.AddSingleton<IngestionPipeline>();
        services.AddSingleton<RegexInvoiceExtractor>();
        services.AddSingleton<IInvoiceExtractor>(sp => ai.Provider == "fake" && configuration.GetValue("Extraction:UseRegexWhenFake", true)
            ? sp.GetRequiredService<RegexInvoiceExtractor>()
            : new LlmInvoiceExtractor(sp.GetRequiredService<Kernel>(), sp.GetRequiredService<ModelPricing>(), ai.ChatModelName,
                sp.GetRequiredService<ICostLedger>(), sp.GetRequiredService<ILogger<LlmInvoiceExtractor>>()));
        services.AddSingleton(sp => new AskService(
            sp.GetRequiredService<Kernel>(), sp.GetRequiredService<IEmbedder>(), sp.GetRequiredService<IDocumentIndex>(),
            sp.GetRequiredService<IDocumentRepository>(), sp.GetRequiredService<IInvoiceExtractor>(), sp.GetRequiredService<ICostLedger>(),
            sp.GetRequiredService<ModelPricing>(), ai.ChatModelName, sp.GetRequiredService<ILogger<AskService>>()));
        return services;
    }

    /// <summary>
    /// Managed identity (DefaultAzureCredential, RBAC on the account) when the service URIs are set, as in Azure;
    /// otherwise the connection string, which is how Azurite is reached locally.
    /// </summary>
    public static (BlobServiceClient Blobs, QueueServiceClient Queues) CreateStorageClients(StorageOptions storage)
    {
        var hasBlob = !string.IsNullOrWhiteSpace(storage.BlobServiceUri);
        var hasQueue = !string.IsNullOrWhiteSpace(storage.QueueServiceUri);
        if (hasBlob != hasQueue)
        {
            throw new InvalidOperationException("set both Storage:BlobServiceUri and Storage:QueueServiceUri, or neither");
        }
        if (hasBlob)
        {
            var credential = new Azure.Identity.DefaultAzureCredential();
            return (new BlobServiceClient(new Uri(storage.BlobServiceUri), credential), new QueueServiceClient(new Uri(storage.QueueServiceUri), credential));
        }
        return (new BlobServiceClient(storage.ConnectionString), new QueueServiceClient(storage.ConnectionString));
    }

    /// <summary>Creates the schema, the blob container and the queue. Called once at start-up by every host.</summary>
    public static async Task InitializeDocAssistantAsync(this IServiceProvider services, CancellationToken ct)
    {
        await services.GetRequiredService<Db>().MigrateAsync(ct);
        await services.GetRequiredService<BlobDocumentStore>().EnsureCreatedAsync(ct);
        await services.GetRequiredService<QueueIngestionQueue>().EnsureCreatedAsync(ct);
    }
}
