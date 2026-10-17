using System.Text.Json;
using Azure.Storage.Queues;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;

namespace DocAssistant.Infrastructure.Storage;

/// <summary>Azure Storage Queue (Azurite locally). The worker polls it; in Azure the Function's blob trigger makes it unnecessary.</summary>
public sealed class QueueIngestionQueue(QueueServiceClient client, string queueName = "ingest") : IIngestionQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private QueueClient Queue => client.GetQueueClient(queueName);

    public async Task EnsureCreatedAsync(CancellationToken ct) => await Queue.CreateIfNotExistsAsync(cancellationToken: ct);

    public async Task EnqueueAsync(IngestionMessage message, CancellationToken ct)
    {
        var body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(message, Json));
        await Queue.SendMessageAsync(body, cancellationToken: ct);
    }

    public async Task<QueuedIngestion?> DequeueAsync(CancellationToken ct)
    {
        var response = await Queue.ReceiveMessageAsync(TimeSpan.FromMinutes(10), ct);
        var m = response.Value;
        if (m is null)
        {
            return null;
        }
        var message = JsonSerializer.Deserialize<IngestionMessage>(Convert.FromBase64String(m.MessageText), Json)
                      ?? throw new InvalidOperationException("malformed ingestion message");
        return new QueuedIngestion(message, m.PopReceipt, m.MessageId, (int)m.DequeueCount);
    }

    public async Task CompleteAsync(QueuedIngestion item, CancellationToken ct) =>
        await Queue.DeleteMessageAsync(item.MessageId, item.Receipt, ct);
}
