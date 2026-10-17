using System.Text.Json;
using DocAssistant.Api.Auth;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Rag;
using DocAssistant.Infrastructure.Postgres;
using Microsoft.AspNetCore.Mvc;

namespace DocAssistant.Api.Endpoints;

public sealed record AskRequest(string Question, Guid? DocumentId);
public sealed record DevTokenRequest(string User);

public static class EndpointExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const long MaxUploadBytes = 20 * 1024 * 1024;

    public static void MapHealth(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
        app.MapGet("/readyz", async (Db db, Azure.Storage.Blobs.BlobServiceClient blobs, CancellationToken ct) =>
        {
            var checks = new Dictionary<string, bool>();
            try
            {
                await using var conn = await db.Source.OpenConnectionAsync(ct);
                await using var cmd = new Npgsql.NpgsqlCommand("select 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                checks["postgres"] = true;
            }
            catch (Exception)
            {
                checks["postgres"] = false;
            }
            try
            {
                await blobs.GetPropertiesAsync(ct);
                checks["storage"] = true;
            }
            catch (Exception)
            {
                checks["storage"] = false;
            }
            var ok = checks.Values.All(v => v);
            return Results.Json(new { status = ok ? "ok" : "degraded", checks }, statusCode: ok ? 200 : 503);
        }).ExcludeFromDescription();
    }

    public static void MapAuth(this WebApplication app, AuthOptions auth)
    {
        if (auth.Mode != "dev")
        {
            return;
        }
        app.MapPost("/auth/dev-token", (DevTokenRequest req, DevTokenIssuer issuer) =>
        {
            if (string.IsNullOrWhiteSpace(req.User) || req.User.Length > 64 || !req.User.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = ["1-64 letters, digits, '-', '_' or '.'"] });
            }
            return Results.Ok(new { token = issuer.Issue(req.User), user = req.User, ownerId = DevTokenIssuer.DeterministicId(req.User) });
        }).RequireRateLimiting("token").WithTags("auth").WithSummary("Development only: mint a bearer token for a named test user");
        app.MapGet("/auth/me", (HttpContext ctx) => Results.Ok(new { ownerId = ctx.User.OwnerId(), name = ctx.User.Identity?.Name }))
            .RequireAuthorization().WithTags("auth");
    }

    public static void MapDocuments(this WebApplication app)
    {
        var group = app.MapGroup("/api/documents").RequireAuthorization().WithTags("documents");

        group.MapPost("/", async (HttpContext ctx, IFormFile file, IDocumentStore store, IDocumentRepository repo, IIngestionQueue queue, CancellationToken ct) =>
        {
            if (file.Length == 0 || file.Length > MaxUploadBytes)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [$"between 1 byte and {MaxUploadBytes / 1024 / 1024} MB"] });
            }
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName) || safeName.Length > 200 || !safeName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["a .pdf file with a name under 200 characters"] });
            }
            await using var stream = file.OpenReadStream();
            var head = new byte[5];
            var read = await stream.ReadAsync(head, ct);
            if (read < 5 || head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F')
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["not a PDF (missing %PDF header)"] });
            }
            stream.Position = 0;

            var owner = ctx.User.OwnerId();
            var id = Guid.NewGuid();
            var blobName = $"{owner}/{id}.pdf";
            await store.SaveAsync(blobName, stream, "application/pdf", ct);
            var record = new DocumentRecord(id, owner, safeName, blobName, file.Length, DocumentKind.Unknown, DocumentStatus.Uploaded, 0, 0, DateTimeOffset.UtcNow, null, null);
            await repo.CreateAsync(record, ct);
            await queue.EnqueueAsync(new IngestionMessage(id, owner, blobName, safeName), ct);
            return Results.Accepted($"/api/documents/{id}", record);
        }).RequireRateLimiting("upload").DisableAntiforgery().WithSummary("Upload a PDF; it is indexed asynchronously");

        group.MapGet("/", async (HttpContext ctx, IDocumentRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(ctx.User.OwnerId(), ct)));

        group.MapGet("/{id:guid}", async (HttpContext ctx, Guid id, IDocumentRepository repo, CancellationToken ct) =>
            await repo.GetAsync(ctx.User.OwnerId(), id, ct) is { } d ? Results.Ok(d) : Results.NotFound());

        group.MapDelete("/{id:guid}", async (HttpContext ctx, Guid id, IDocumentRepository repo, IDocumentStore store, IDocumentIndex index, CancellationToken ct) =>
        {
            var owner = ctx.User.OwnerId();
            var doc = await repo.GetAsync(owner, id, ct);
            if (doc is null)
            {
                return Results.NotFound();
            }
            await index.DeleteDocumentAsync(id, ct);
            await store.DeleteAsync(doc.BlobName, ct);
            await repo.DeleteAsync(owner, id, ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/pages", async (HttpContext ctx, Guid id, IDocumentRepository repo, CancellationToken ct) =>
        {
            var pages = await repo.GetPagesAsync(ctx.User.OwnerId(), id, ct);
            return pages.Count == 0 ? Results.NotFound() : Results.Ok(pages);
        });

        group.MapPost("/{id:guid}/extract", async (HttpContext ctx, Guid id, IDocumentRepository repo, IInvoiceExtractor extractor, CancellationToken ct) =>
        {
            var owner = ctx.User.OwnerId();
            var doc = await repo.GetAsync(owner, id, ct);
            if (doc is null)
            {
                return Results.NotFound();
            }
            if (doc.Status != DocumentStatus.Ready)
            {
                return Results.Conflict(new { error = $"document is {doc.Status}; wait until it is Ready" });
            }
            var pages = await repo.GetPagesAsync(owner, id, ct);
            var result = await extractor.ExtractAsync(pages, owner, ct);
            await repo.SaveInvoiceFieldsAsync(id, result.Fields, result.Method, ct);
            return Results.Ok(new { documentId = id, result.Method, result.Fields, result.Usage, elapsedMs = (int)result.Elapsed.TotalMilliseconds });
        }).RequireRateLimiting("ask").WithSummary("Extract invoice fields (model when configured, regex baseline otherwise)");

        group.MapGet("/{id:guid}/fields", async (HttpContext ctx, Guid id, IDocumentRepository repo, CancellationToken ct) =>
            await repo.GetInvoiceFieldsAsync(ctx.User.OwnerId(), id, ct) is { } f
                ? Results.Ok(new { documentId = id, f.Method, f.Fields })
                : Results.NotFound());
    }

    public static void MapAsk(this WebApplication app)
    {
        var group = app.MapGroup("/api/ask").RequireAuthorization().RequireRateLimiting("ask").WithTags("ask");

        group.MapPost("/", async (HttpContext ctx, AskRequest req, AskService ask, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > 2000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["question"] = ["1 to 2000 characters"] });
            }
            var answer = await ask.AskAsync(ctx.User.OwnerId(), req.Question.Trim(), req.DocumentId, ct);
            return Results.Ok(AnswerView(answer));
        }).WithSummary("Answer a question over the user's documents with citations");

        group.MapPost("/stream", async (HttpContext ctx, AskRequest req, AskService ask, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > 2000)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var ev in ask.StreamAsync(ctx.User.OwnerId(), req.Question.Trim(), req.DocumentId, ct))
            {
                object payload = ev.Type switch
                {
                    "token" => new { text = ev.Text },
                    "tool" => new { name = ev.Text, arguments = ev.Data },
                    "done" when ev.Data is Answer a => AnswerView(a),
                    _ => ev.Data ?? new { },
                };
                await ctx.Response.WriteAsync($"event: {ev.Type}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).WithSummary("Same as /api/ask, streamed as server-sent events: sources, token, tool, done");
    }

    public static void MapCosts(this WebApplication app)
    {
        app.MapGet("/api/costs", async (HttpContext ctx, [FromQuery] int? days, ICostLedger ledger, CancellationToken ct) =>
        {
            var window = Math.Clamp(days ?? 30, 1, 365);
            var summary = await ledger.SummaryAsync(ctx.User.OwnerId(), DateTimeOffset.UtcNow.AddDays(-window), ct);
            return Results.Ok(new { days = window, summary });
        }).RequireAuthorization().WithTags("costs").WithSummary("Token spend for the signed-in user");
    }

    public static object AnswerView(Answer a) => new
    {
        answer = a.Text,
        citations = a.Citations,
        sources = a.Sources.Select(AskService.SourceView),
        toolCalls = a.ToolCalls,
        usage = a.Usage,
        latencyMs = (int)a.Elapsed.TotalMilliseconds,
        model = a.Model,
    };
}
