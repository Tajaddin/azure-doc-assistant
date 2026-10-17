using System.Text.Json;
using DocAssistant.SynthData;

// Writes the synthetic corpus and the eval questions:
//   dotnet run --project tools/DocAssistant.SynthData -- [--out data/synthetic] [--questions evals/questions.json]
var outDir = "data/synthetic";
var questionsPath = "evals/questions.json";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--out")
    {
        outDir = args[i + 1];
    }
    if (args[i] == "--questions")
    {
        questionsPath = args[i + 1];
    }
}

var corpus = SyntheticCorpus.Generate();
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
Directory.CreateDirectory(Path.Combine(outDir, "invoices"));
Directory.CreateDirectory(Path.Combine(outDir, "contracts"));
Directory.CreateDirectory(Path.Combine(outDir, "manuals"));
Directory.CreateDirectory(Path.GetDirectoryName(questionsPath)!);

var pages = 0;
foreach (var inv in corpus.Invoices)
{
    File.WriteAllBytes(Path.Combine(outDir, "invoices", inv.FileName), SyntheticCorpus.RenderInvoice(inv));
    pages += 1;
}
foreach (var k in corpus.Contracts)
{
    File.WriteAllBytes(Path.Combine(outDir, "contracts", k.FileName), SyntheticCorpus.RenderContract(k));
    pages += 1;
}
foreach (var m in corpus.Manuals)
{
    File.WriteAllBytes(Path.Combine(outDir, "manuals", m.FileName), SyntheticCorpus.RenderManual(m));
    pages += 2;
}
File.WriteAllText(Path.Combine(outDir, "truth.json"), JsonSerializer.Serialize(new
{
    generated_with = "tools/DocAssistant.SynthData (seed 42)",
    invoices = corpus.Invoices.Select(i => new { file = i.FileName, fields = i.Truth }),
    contracts = corpus.Contracts,
    manuals = corpus.Manuals,
}, json));
var questions = SyntheticCorpus.Questions(corpus);
File.WriteAllText(questionsPath, JsonSerializer.Serialize(new { count = questions.Count, questions }, json));
Console.WriteLine($"wrote {corpus.Invoices.Count} invoices, {corpus.Contracts.Count} contracts, {corpus.Manuals.Count} manuals ({pages} pages) to {outDir}; {questions.Count} questions to {questionsPath}");
