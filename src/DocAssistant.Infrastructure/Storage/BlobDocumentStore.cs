using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocAssistant.Core.Abstractions;

namespace DocAssistant.Infrastructure.Storage;

/// <summary>Azure Blob Storage; identical against Azurite locally. The container is private; nothing is served by URL.</summary>
public sealed class BlobDocumentStore(BlobServiceClient client, string containerName = "documents") : IDocumentStore
{
    private BlobContainerClient Container => client.GetBlobContainerClient(containerName);

    public async Task EnsureCreatedAsync(CancellationToken ct) =>
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

    /// <summary>
    /// Readiness probe: Get Container Properties, which needs blobServices/containers/read (in Storage Blob Data
    /// Contributor). The account-level Get Blob Service Properties needs blobServices/read, which no data role
    /// grants, so it fails with 403 under the managed identity.
    /// </summary>
    public async Task ProbeAsync(CancellationToken ct) =>
        await Container.GetPropertiesAsync(cancellationToken: ct);

    public async Task SaveAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        var blob = Container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        }, ct);
    }

    public async Task<Stream> OpenAsync(string blobName, CancellationToken ct)
    {
        var blob = Container.GetBlobClient(blobName);
        var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct) =>
        await Container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
}
