using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocAssistant.Core.Abstractions;
using DocAssistant.Core.Domain;
using DocAssistant.SynthData;
using DocAssistant.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAssistant.IntegrationTests;

[Collection("stack")]
public sealed class ApiTests(StackFixture stack) : IAsyncLifetime
{
    private static readonly SyntheticCorpus.Corpus Corpus = SyntheticCorpus.Generate();
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(stack);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<HttpClient> UserAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/auth/dev-token", new { user = name });
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonNode> UploadAsync(HttpClient client, string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", fileName);
        var resp = await client.PostAsync("/api/documents", form);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    [Fact]
    public async Task Health_ready_metrics_and_auth_gates()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
        var ready = await _client.GetFromJsonAsync<JsonNode>("/readyz");
        Assert.Equal("ok", ready!["status"]!.GetValue<string>());
        Assert.True(ready["checks"]!["postgres"]!.GetValue<bool>());
        Assert.True(ready["checks"]!["storage"]!.GetValue<bool>());
        Assert.Contains("http_server", await _client.GetStringAsync("/metrics"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/ask", new { question = "x" })).StatusCode);
        var bad = await _client.PostAsJsonAsync("/auth/dev-token", new { user = "has space" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var alice = await UserAsync("alice");
        var me = await alice.GetFromJsonAsync<JsonNode>("/auth/me");
        Assert.Equal("alice", me!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upload_validation_rejects_non_pdfs_and_bad_names()
    {
        var alice = await UserAsync("alice");
        using var form = new MultipartFormDataContent { { new ByteArrayContent("not a pdf"u8.ToArray()), "file", "x.pdf" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/documents", form)).StatusCode);
        using var form2 = new MultipartFormDataContent { { new ByteArrayContent("%PDF-1.4 ..."u8.ToArray()), "file", "x.txt" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/documents", form2)).StatusCode);
        var tooLong = await alice.PostAsJsonAsync("/api/ask", new { question = new string('q', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Upload_ingest_ask_extract_delete_end_to_end()
    {
        var user = await UserAsync("carol");
        var inv = Corpus.Invoices[0];
        var uploaded = await UploadAsync(user, inv.FileName, SyntheticCorpus.RenderInvoice(inv));
        var id = uploaded["id"]!.GetValue<string>();
        Assert.Equal("Uploaded", uploaded["status"]!.GetValue<string>());

        Assert.Equal(1, await _factory.DrainIngestionQueueAsync());
        var doc = await user.GetFromJsonAsync<JsonNode>($"/api/documents/{id}");
        Assert.Equal("Ready", doc!["status"]!.GetValue<string>());
        Assert.Equal("Invoice", doc["kind"]!.GetValue<string>());
        Assert.Equal(1, doc["pageCount"]!.GetValue<int>());
        Assert.True(doc["chunkCount"]!.GetValue<int>() >= 1);
        var pages = await user.GetFromJsonAsync<JsonArray>($"/api/documents/{id}/pages");
        Assert.Contains(inv.Number, pages![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var ask = await user.PostAsJsonAsync("/api/ask", new { question = $"What is the total amount due on invoice {inv.Number}?" });
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var answer = (await ask.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Contains("[1]", answer["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        var citation = Assert.Single(answer["citations"]!.AsArray());
        Assert.Equal(inv.FileName, citation!["fileName"]!.GetValue<string>());
        Assert.Equal(1, citation["page"]!.GetValue<int>());
        Assert.True(answer["usage"]!["calls"]!.GetValue<int>() >= 1);
        Assert.Equal("fake", answer["model"]!.GetValue<string>());

        var extract = await user.PostAsync($"/api/documents/{id}/extract", null);
        Assert.Equal(HttpStatusCode.OK, extract.StatusCode);
        var fields = (await extract.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("regex", fields["method"]!.GetValue<string>());
        Assert.Equal(inv.Number, fields["fields"]!["invoice_number"]!.GetValue<string>());
        Assert.Equal(inv.Total, fields["fields"]!["total"]!.GetValue<decimal>());
        var stored = await user.GetFromJsonAsync<JsonNode>($"/api/documents/{id}/fields");
        Assert.Equal(inv.Vendor, stored!["fields"]!["vendor"]!.GetValue<string>());

        var costs = await user.GetFromJsonAsync<JsonNode>("/api/costs?days=1");
        Assert.True(costs!["summary"]!["calls"]!.GetValue<int>() >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/documents/{id}")).StatusCode);
        var after = await (await user.PostAsJsonAsync("/api/ask", new { question = "total?" })).Content.ReadFromJsonAsync<JsonNode>();
        Assert.Empty(after!["sources"]!.AsArray());
    }

    [Fact]
    public async Task Users_cannot_see_each_others_documents()
    {
        var dave = await UserAsync("dave");
        var erin = await UserAsync("erin");
        var k = Corpus.Contracts[0];
        var uploaded = await UploadAsync(dave, k.FileName, SyntheticCorpus.RenderContract(k));
        var id = uploaded["id"]!.GetValue<string>();
        await _factory.DrainIngestionQueueAsync();

        Assert.NotEmpty((await dave.GetFromJsonAsync<JsonArray>("/api/documents"))!);
        Assert.Empty((await erin.GetFromJsonAsync<JsonArray>("/api/documents"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await erin.GetAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erin.PostAsync($"/api/documents/{id}/extract", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erin.DeleteAsync($"/api/documents/{id}")).StatusCode);

        var daveAnswer = await (await dave.PostAsJsonAsync("/api/ask", new { question = "How many days notice to terminate?" })).Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotEmpty(daveAnswer!["sources"]!.AsArray());
        var erinAnswer = await (await erin.PostAsJsonAsync("/api/ask", new { question = "How many days notice to terminate?" })).Content.ReadFromJsonAsync<JsonNode>();
        Assert.Empty(erinAnswer!["sources"]!.AsArray());
        Assert.Empty(erinAnswer["citations"]!.AsArray());
    }

    [Fact]
    public async Task Streaming_emits_sources_tokens_and_done()
    {
        var frank = await UserAsync("frank");
        var m = Corpus.Manuals[0];
        await UploadAsync(frank, m.FileName, SyntheticCorpus.RenderManual(m));
        await _factory.DrainIngestionQueueAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ask/stream") { Content = JsonContent.Create(new { question = "What does error code E2 mean?" }) };
        using var resp = await frank.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        var body = await resp.Content.ReadAsStringAsync();
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split('\n')[0].Replace("event: ", "")).ToList();
        Assert.Equal("sources", events[0]);
        Assert.Contains("token", events);
        Assert.Equal("done", events[^1]);
        Assert.Contains("\"citations\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_answers_429()
    {
        using var tight = new ApiFactory(stack, new Dictionary<string, string?> { ["RateLimits:AskPerMinute"] = "1" });
        using var anon = tight.CreateClient();
        var tokenResp = await anon.PostAsJsonAsync("/auth/dev-token", new { user = "gus" });
        var token = (await tokenResp.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
        using var gus = tight.CreateClient();
        gus.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var first = await gus.PostAsJsonAsync("/api/ask", new { question = "hello" });
        var second = await gus.PostAsJsonAsync("/api/ask", new { question = "hello" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gus.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Worker_gives_up_on_a_poison_message_and_marks_the_document_failed()
    {
        var services = _factory.Services;
        var repo = services.GetRequiredService<IDocumentRepository>();
        var queue = services.GetRequiredService<IIngestionQueue>();
        var id = Guid.NewGuid();
        await repo.CreateAsync(new DocumentRecord(id, "poison-owner", "ghost.pdf", "poison-owner/missing.pdf", 10, DocumentKind.Unknown, DocumentStatus.Uploaded, 0, 0, DateTimeOffset.UtcNow, null, null), default);
        await queue.EnqueueAsync(new IngestionMessage(id, "poison-owner", "poison-owner/missing.pdf", "ghost.pdf"), default);
        var item = await queue.DequeueAsync(default);
        Assert.NotNull(item);
        var worker = new IngestionWorker(queue, services.GetRequiredService<DocAssistant.Core.Ingestion.IngestionPipeline>(), repo, NullLogger<IngestionWorker>.Instance);

        await worker.ProcessAsync(item with { DequeueCount = IngestionWorker.MaxAttempts }, default);

        var doc = await repo.GetAsync("poison-owner", id, default);
        Assert.Equal(DocumentStatus.Failed, doc!.Status);
        Assert.Contains("failed after", doc.Error, StringComparison.Ordinal);
        Assert.Null(await queue.DequeueAsync(default)); // completed, not redelivered
    }
}
