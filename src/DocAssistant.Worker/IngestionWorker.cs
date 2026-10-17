using System.Diagnostics;
using System.Diagnostics.Metrics;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Ingestion;

namespace DocAssistant.Worker;

/// <summary>
/// Polls the ingestion queue and runs the pipeline. The Azure Function does the same on a blob trigger; this
/// host exists so the whole system runs locally and in CI without the Functions runtime.
/// </summary>
public sealed class IngestionWorker(
    IIngestionQueue queue,
    IngestionPipeline pipeline,
    IDocumentRepository documents,
    ILogger<IngestionWorker> log) : BackgroundService
{
    public static readonly ActivitySource Activity = new("DocAssistant.Ingestion");
    public static readonly Meter Meter = new("DocAssistant.Ingestion");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("docassistant.ingest.duration", "s");
    private static readonly Counter<long> Pages = Meter.CreateCounter<long>("docassistant.ingest.pages");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("docassistant.ingest.count");

    public const int MaxAttempts = 5;
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("ingestion worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedIngestion? item = null;
            try
            {
                item = await queue.DequeueAsync(stoppingToken);
                if (item is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ingestion loop error for message {MessageId}", item?.MessageId);
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
        log.LogInformation("ingestion worker stopped");
    }

    public async Task ProcessAsync(QueuedIngestion item, CancellationToken ct)
    {
        using var span = Activity.StartActivity("ingest");
        span?.SetTag("document.id", item.Message.DocumentId);
        span?.SetTag("attempt", item.DequeueCount);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await pipeline.RunAsync(item.Message, ct);
            await queue.CompleteAsync(item, ct);
            Pages.Add(result.Pages);
            Duration.Record(result.Total.TotalSeconds, new KeyValuePair<string, object?>("outcome", "ok"));
            Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
            span?.SetTag("pages", result.Pages);
            span?.SetTag("pages_per_minute", Math.Round(result.PagesPerMinute, 1));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Duration.Record(sw.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", "error"));
            Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
            if (item.DequeueCount >= MaxAttempts)
            {
                log.LogError(ex, "giving up on {DocumentId} after {Attempts} attempts", item.Message.DocumentId, item.DequeueCount);
                await documents.SetStatusAsync(item.Message.DocumentId, DocumentStatus.Failed, $"failed after {item.DequeueCount} attempts: {ex.Message}", CancellationToken.None);
                await queue.CompleteAsync(item, CancellationToken.None); // poison: drop it
            }
            else
            {
                log.LogWarning(ex, "attempt {Attempt} failed for {DocumentId}; the message becomes visible again", item.DequeueCount, item.Message.DocumentId);
            }
        }
    }
}
