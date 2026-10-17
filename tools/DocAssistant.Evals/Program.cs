using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocAssistant.Core.Domain;
using DocAssistant.Core.Evals;
using DocAssistant.SynthData;

// Evaluates a running API end to end: uploads the synthetic corpus for a fresh user, waits for ingestion,
// asks every question, runs extraction on every invoice, and scores all of it deterministically.
//
//   dotnet run --project tools/DocAssistant.Evals -- --api http://localhost:8080 [--label ollama-qwen3.5-4b] [--gate]
//
// Numbers are labelled with the model the API reports. Nothing here asks a model to grade a model.

var api = Arg("--api") ?? "http://localhost:8080";
var label = Arg("--label");
var outDir = Arg("--out") ?? "evals/results";
var gate = args.Contains("--gate");
var questionsPath = Arg("--questions") ?? "evals/questions.json";
var timeoutMinutes = int.Parse(Arg("--timeout-minutes") ?? "20", CultureInfo.InvariantCulture);

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var corpus = SyntheticCorpus.Generate();
var questions = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(questionsPath))!["questions"]!.AsArray()
    .Select(q => q!.Deserialize<SyntheticCorpus.EvalQuestion>()!).ToList();

using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromMinutes(5) };
var user = $"eval-{DateTime.UtcNow:yyyyMMddHHmmss}";
var tokenResp = await http.PostAsJsonAsync("/auth/dev-token", new { user });
tokenResp.EnsureSuccessStatusCode();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await tokenResp.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>());

// ---------------------------------------------------------------- 1. ingestion throughput
var files = new List<(string Name, byte[] Bytes, int Pages)>();
files.AddRange(corpus.Invoices.Select(i => (i.FileName, SyntheticCorpus.RenderInvoice(i), 1)));
files.AddRange(corpus.Contracts.Select(k => (k.FileName, SyntheticCorpus.RenderContract(k), 1)));
files.AddRange(corpus.Manuals.Select(m => (m.FileName, SyntheticCorpus.RenderManual(m), 2)));
var totalPages = files.Sum(f => f.Pages);
var ingestTimer = Stopwatch.StartNew();
var ids = new Dictionary<string, Guid>();
foreach (var (name, bytes, _) in files)
{
    using var form = new MultipartFormDataContent();
    var content = new ByteArrayContent(bytes);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    form.Add(content, "file", name);
    var resp = await http.PostAsync("/api/documents", form);
    resp.EnsureSuccessStatusCode();
    ids[name] = Guid.Parse((await resp.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>());
}
var uploadSeconds = ingestTimer.Elapsed.TotalSeconds;
var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
JsonArray docs;
while (true)
{
    docs = (await http.GetFromJsonAsync<JsonArray>("/api/documents"))!;
    var statuses = docs.Select(d => d!["status"]!.GetValue<string>()).ToList();
    if (statuses.All(s => s is "Ready" or "Failed"))
    {
        break;
    }
    if (DateTime.UtcNow > deadline)
    {
        Console.Error.WriteLine("ingestion did not finish in time");
        return 2;
    }
    await Task.Delay(1000);
}
ingestTimer.Stop();
var failed = docs.Where(d => d!["status"]!.GetValue<string>() == "Failed").Select(d => d!["fileName"]!.GetValue<string>()).ToList();
var ready = docs.Count - failed.Count;
var pagesPerMinute = totalPages / ingestTimer.Elapsed.TotalMinutes;
Console.WriteLine($"ingested {ready}/{docs.Count} documents, {totalPages} pages in {ingestTimer.Elapsed.TotalSeconds:F1} s ({pagesPerMinute:F1} pages/min, uploads took {uploadSeconds:F1} s)");

// ---------------------------------------------------------------- 2. questions
var rows = new List<Dictionary<string, object?>>();
var latencies = new List<double>();
string model = label ?? "unknown";
foreach (var q in questions)
{
    var sw = Stopwatch.StartNew();
    JsonNode answer;
    try
    {
        var resp = await http.PostAsJsonAsync("/api/ask", new { question = q.Question });
        resp.EnsureSuccessStatusCode();
        answer = (await resp.Content.ReadFromJsonAsync<JsonNode>())!;
    }
    catch (Exception ex)
    {
        rows.Add(new() { ["id"] = q.Id, ["category"] = q.Category, ["question"] = q.Question, ["error"] = ex.Message, ["correct"] = false, ["cited_expected"] = false });
        Console.WriteLine($"ERR  {q.Id} {ex.Message}");
        continue;
    }
    sw.Stop();
    latencies.Add(sw.Elapsed.TotalMilliseconds);
    var text = answer["answer"]!.GetValue<string>();
    model = answer["model"]!.GetValue<string>();
    var citations = answer["citations"]!.AsArray().Select(c => new Citation(
        c!["marker"]!.GetValue<int>(), Guid.Parse(c["documentId"]!.GetValue<string>()), c["fileName"]!.GetValue<string>(),
        c["page"]!.GetValue<int>(), Guid.Parse(c["chunkId"]!.GetValue<string>()), c["snippet"]!.GetValue<string>())).ToList();
    bool correct;
    if (q.Category == "unanswerable")
    {
        correct = q.AcceptAny!.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
    else if (q.AcceptAny is { Count: > 0 })
    {
        correct = q.AcceptAny.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)) || (q.Expected is not null && Scoring.AnswerMatches(q.Expected, text));
    }
    else
    {
        correct = Scoring.AnswerMatches(q.Expected!, text);
    }
    var citedExpected = q.ExpectedFile is null ? citations.Count == 0 : Scoring.CitesExpectedDocument(citations, q.ExpectedFile);
    var usage = answer["usage"]!;
    rows.Add(new()
    {
        ["id"] = q.Id,
        ["category"] = q.Category,
        ["question"] = q.Question,
        ["expected"] = q.Expected,
        ["expected_file"] = q.ExpectedFile,
        ["answer"] = text,
        ["correct"] = correct,
        ["cited_expected"] = citedExpected,
        ["citations"] = citations.Count,
        ["tool_calls"] = answer["toolCalls"]!.AsArray().Select(t => t!.GetValue<string>()).ToList(),
        ["latency_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds),
        ["input_tokens"] = usage["inputTokens"]!.GetValue<int>(),
        ["output_tokens"] = usage["outputTokens"]!.GetValue<int>(),
        ["cost_usd"] = usage["costUsd"]!.GetValue<decimal>(),
        ["estimated_usage"] = usage["estimated"]!.GetValue<bool>(),
    });
    Console.WriteLine($"{(correct ? "PASS" : "FAIL")} {q.Id} cited={citedExpected,-5} {sw.ElapsedMilliseconds,6} ms  {q.Question[..Math.Min(70, q.Question.Length)]}");
    if (!correct)
    {
        Console.WriteLine($"       expected: {q.Expected}  got: {text[..Math.Min(160, text.Length)].Replace('\n', ' ')}");
    }
}

// ---------------------------------------------------------------- 3. extraction
var extraction = new List<Dictionary<string, object?>>();
foreach (var inv in corpus.Invoices)
{
    if (!ids.TryGetValue(inv.FileName, out var id) || failed.Contains(inv.FileName))
    {
        continue;
    }
    var sw = Stopwatch.StartNew();
    var resp = await http.PostAsync($"/api/documents/{id}/extract", null);
    sw.Stop();
    if (!resp.IsSuccessStatusCode)
    {
        extraction.Add(new() { ["file"] = inv.FileName, ["error"] = (int)resp.StatusCode });
        continue;
    }
    var body = (await resp.Content.ReadFromJsonAsync<JsonNode>())!;
    var got = body["fields"]!.Deserialize<InvoiceFields>()!;
    var (ok, total) = Scoring.ExtractionAgreement(inv.Truth, got);
    extraction.Add(new() { ["file"] = inv.FileName, ["method"] = body["method"]!.GetValue<string>(), ["correct_fields"] = ok, ["total_fields"] = total, ["latency_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds), ["cost_usd"] = body["usage"]!["costUsd"]!.GetValue<decimal>() });
}

// ---------------------------------------------------------------- 4. summary
var answerable = rows.Where(r => (string)r["category"]! != "unanswerable").ToList();
var unanswerable = rows.Where(r => (string)r["category"]! == "unanswerable").ToList();
double Rate(IEnumerable<Dictionary<string, object?>> rs, string key) => rs.Any() ? rs.Count(r => r[key] is true) / (double)rs.Count() : 0;
var fieldsOk = extraction.Sum(e => e.TryGetValue("correct_fields", out var v) ? (int)v! : 0);
var fieldsAll = extraction.Sum(e => e.TryGetValue("total_fields", out var v) ? (int)v! : 0);
var summary = new Dictionary<string, object?>
{
    ["model"] = model,
    ["questions"] = rows.Count,
    ["answer_accuracy"] = Math.Round(Rate(answerable, "correct"), 4),
    ["answer_accuracy_n"] = answerable.Count,
    ["citation_rate"] = Math.Round(Rate(answerable, "cited_expected"), 4),
    ["unanswerable_handled"] = Math.Round(Rate(unanswerable, "correct"), 4),
    ["unanswerable_n"] = unanswerable.Count,
    ["by_category"] = rows.GroupBy(r => (string)r["category"]!).ToDictionary(g => g.Key, g => Math.Round(Rate(g, "correct"), 4)),
    ["latency_p50_ms"] = Math.Round(Scoring.Percentile(latencies, 0.5)),
    ["latency_p95_ms"] = Math.Round(Scoring.Percentile(latencies, 0.95)),
    ["tool_calls_total"] = rows.Sum(r => r.TryGetValue("tool_calls", out var t) ? ((List<string>)t!).Count : 0),
    ["cost_usd_total"] = rows.Sum(r => r.TryGetValue("cost_usd", out var c) ? (decimal)c! : 0m),
    ["usage_estimated"] = rows.Any(r => r.TryGetValue("estimated_usage", out var e) && e is true),
    ["extraction_field_accuracy"] = fieldsAll > 0 ? Math.Round(fieldsOk / (double)fieldsAll, 4) : 0,
    ["extraction_fields"] = $"{fieldsOk}/{fieldsAll}",
    ["extraction_methods"] = extraction.GroupBy(e => e.TryGetValue("method", out var m) ? (string)m! : "error").ToDictionary(g => g.Key, g => g.Count()),
    ["documents_ready"] = ready,
    ["documents_failed"] = failed,
    ["pages"] = totalPages,
    ["ingestion_seconds"] = Math.Round(ingestTimer.Elapsed.TotalSeconds, 1),
    ["pages_per_minute"] = Math.Round(pagesPerMinute, 1),
};
var slug = (label ?? model).Replace(':', '-').Replace('/', '-').Replace('.', '_');
Directory.CreateDirectory(outDir);
var report = new { run_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), api, user, summary, questions = rows, extraction };
File.WriteAllText(Path.Combine(outDir, $"latest-{slug}.json"), JsonSerializer.Serialize(report, json));
var md = new StringBuilder();
md.AppendLine("| Metric | Value |").AppendLine("|---|---|");
md.AppendLine(CultureInfo.InvariantCulture, $"| Model | `{model}` |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Run (UTC) | {report.run_utc} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Answer accuracy (n={answerable.Count}) | {(double)summary["answer_accuracy"]!:P1} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Citation rate (cites the expected document) | {(double)summary["citation_rate"]!:P1} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Unanswerable handled (n={unanswerable.Count}) | {(double)summary["unanswerable_handled"]!:P1} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Invoice extraction field accuracy | {(double)summary["extraction_field_accuracy"]!:P1} ({summary["extraction_fields"]}) |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Latency p50 / p95 | {summary["latency_p50_ms"]} ms / {summary["latency_p95_ms"]} ms |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Tool calls | {summary["tool_calls_total"]} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Cost (list prices; $0 for local models) | ${summary["cost_usd_total"]:F4}{((bool)summary["usage_estimated"]! ? " (token counts estimated)" : "")} |");
md.AppendLine(CultureInfo.InvariantCulture, $"| Ingestion | {ready}/{docs.Count} documents, {totalPages} pages in {summary["ingestion_seconds"]} s = {summary["pages_per_minute"]} pages/min |");
File.WriteAllText(Path.Combine(outDir, $"latest-{slug}.md"), md.ToString());
Console.WriteLine();
Console.Write(md);

if (gate)
{
    var baselinePath = Path.Combine(Path.GetDirectoryName(questionsPath)!, "baseline.json");
    var baseline = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(baselinePath))!;
    var key = baseline[slug] is not null ? slug : "default";
    var thresholds = baseline[key]!.AsObject();
    var failures = new List<string>();
    foreach (var (metric, min) in thresholds)
    {
        var value = summary[metric] switch { double d => d, int i => i, decimal m => (double)m, _ => 0 };
        if (value < min!.GetValue<double>())
        {
            failures.Add($"{metric}={value} < {min}");
        }
    }
    if (failures.Count > 0)
    {
        Console.WriteLine($"GATE FAILED ({key}): " + string.Join("; ", failures));
        return 1;
    }
    Console.WriteLine($"GATE PASSED ({key})");
}
return 0;
