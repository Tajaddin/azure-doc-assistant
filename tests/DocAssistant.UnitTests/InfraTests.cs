using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using DocAssistant.Api.Endpoints;
using DocAssistant.Infrastructure;
using DocAssistant.Infrastructure.Postgres;
using DocAssistant.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DocAssistant.UnitTests;

/// <summary>Files in the repository, for tests that check committed configuration rather than code.</summary>
public static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocAssistant.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>The Bicep templates cannot be deployed from the tests; these pin the properties that broke or exposed them.</summary>
public class InfraTests
{
    [Fact]
    public void Deploy_identity_trusts_the_subject_github_sends_for_the_deploy_job()
    {
        // A job that names an environment gets the subject repo:<owner>/<repo>:environment:<name>, not the branch.
        var environment = Regex.Match(RepoFiles.Read(".github", "workflows", "deploy.yml"), @"(?m)^\s+environment:\s*(\S+)\s*$").Groups[1].Value;
        Assert.Equal("production", environment);
        var subject = Regex.Match(RepoFiles.Read("infra", "bicep", "resources.bicep"), @"subject:\s*'([^']+)'").Groups[1].Value;
        Assert.Equal($"repo:${{githubRepository}}:environment:{environment}", subject);
    }

    [Fact]
    public void Postgres_stays_reachable_from_the_consumption_function_and_verifies_the_server_certificate()
    {
        var bicep = RepoFiles.Read("infra", "bicep", "resources.bicep");
        // On Consumption, scaling can give the Function outbound addresses it did not report, so a firewall
        // built from them drops ingestion's connections; until the Function has fixed or private egress the
        // server admits Azure services (0.0.0.0) and relies on the password and TLS (ADR 0005).
        Assert.Contains("sku: { name: 'Y1', tier: 'Dynamic' }", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("func.properties.possibleOutboundIpAddresses", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("func.properties.outboundIpAddresses", bicep, StringComparison.Ordinal);
        Assert.Matches(@"startIpAddress:\s*'0\.0\.0\.0',\s*endIpAddress:\s*'0\.0\.0\.0'", bicep);
        Assert.DoesNotContain("Trust Server Certificate", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SSL Mode=VerifyFull", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingestion_function_gets_the_postgres_connection_string_from_key_vault()
    {
        // The Function host does not load Key Vault into configuration (the API does), so it needs a reference.
        var bicep = RepoFiles.Read("infra", "bicep", "resources.bicep");
        var func = bicep[bicep.IndexOf("resource func 'Microsoft.Web/sites", StringComparison.Ordinal)..];
        Assert.Contains("{ name: 'Postgres__ConnectionString', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=Postgres--ConnectionString)' }", func, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAzureKeyVault", RepoFiles.Read("src", "DocAssistant.Functions", "Program.cs"), StringComparison.Ordinal);
    }
}

public class StorageClientTests
{
    [Fact]
    public void Service_uris_select_the_managed_identity_even_when_a_connection_string_is_also_set()
    {
        var options = new StorageOptions
        {
            ConnectionString = "UseDevelopmentStorage=true", // appsettings.json default, still present in Azure
            BlobServiceUri = "https://stdocs.blob.core.windows.net/",
            QueueServiceUri = "https://stdocs.queue.core.windows.net/",
        };
        var (blobs, queues) = DependencyInjection.CreateStorageClients(options);
        Assert.Equal(new Uri(options.BlobServiceUri), blobs.Uri);
        Assert.Equal(new Uri(options.QueueServiceUri), queues.Uri);
        // a client built from a token credential holds no account key, so it cannot sign SAS tokens
        Assert.False(blobs.CanGenerateAccountSasUri);
        Assert.False(queues.CanGenerateAccountSasUri);
    }

    [Fact]
    public void Without_service_uris_the_connection_string_is_used_and_half_a_configuration_fails()
    {
        var (blobs, _) = DependencyInjection.CreateStorageClients(new StorageOptions { ConnectionString = "UseDevelopmentStorage=true" });
        Assert.True(blobs.CanGenerateAccountSasUri); // Azurite's well-known account key
        Assert.Throws<InvalidOperationException>(() => DependencyInjection.CreateStorageClients(new StorageOptions { BlobServiceUri = "https://stdocs.blob.core.windows.net/" }));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Readyz_probes_storage_with_a_container_read_that_the_data_role_allows()
    {
        // The API's identity has Storage Blob Data Contributor: container reads, but not Get Blob Service
        // Properties (blobServices/read), which would answer 403 in Azure and fail /readyz on every deploy.
        var handler = new RecordingHandler();
        var blobs = new BlobServiceClient(new Uri("https://stdocs.blob.core.windows.net/"), new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) });
        var db = new Db("Host=localhost;Database=docs", 256);
        await db.DisposeAsync(); // fails at once; only the storage check is under test
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(blobs); // as in the app, so a handler asking for the service client gets this one
        builder.Services.AddSingleton(new BlobDocumentStore(blobs, "documents"));
        await using var app = builder.Build();
        app.MapHealth();
        var readyz = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/readyz");
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Response.Body = new MemoryStream();

        await readyz.RequestDelegate!(ctx);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/documents", request.AbsolutePath);
        Assert.Contains("restype=container", request.Query, StringComparison.Ordinal);
        ctx.Response.Body.Position = 0;
        var checks = JsonNode.Parse(ctx.Response.Body)!["checks"]!;
        Assert.True(checks["storage"]!.GetValue<bool>());
        Assert.False(checks["postgres"]!.GetValue<bool>());
    }

    [Fact]
    public void Bicep_gives_the_apps_storage_endpoints_and_no_account_key()
    {
        var bicep = RepoFiles.Read("infra", "bicep", "resources.bicep");
        Assert.DoesNotContain("listKeys", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountKey", bicep, StringComparison.Ordinal);
        Assert.Contains("Storage__BlobServiceUri", bicep, StringComparison.Ordinal);
        Assert.Contains("Storage__QueueServiceUri", bicep, StringComparison.Ordinal);
    }
}
