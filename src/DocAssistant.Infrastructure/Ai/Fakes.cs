using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DocAssistant.Infrastructure.Ai;

/// <summary>
/// Deterministic chat service for tests and key-less CI. It never calls tools. For an answer prompt it
/// quotes the first source and cites it as [1]; for the extraction prompt it answers with the regex
/// extractor's JSON, so the API's full surface works without a model. Says nothing about model quality.
/// </summary>
public sealed partial class FakeChatCompletionService : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?> { ["ModelId"] = "fake" };

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        var text = Respond(chatHistory);
        var message = new ChatMessageContent(AuthorRole.Assistant, text, modelId: "fake", metadata: Usage(chatHistory, text));
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([message]);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = Respond(chatHistory);
        foreach (var word in text.Split(' '))
        {
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, word + " ", modelId: "fake");
            await Task.Yield();
        }
        yield return new StreamingChatMessageContent(AuthorRole.Assistant, string.Empty, modelId: "fake", metadata: Usage(chatHistory, text));
    }

    private static Dictionary<string, object?> Usage(ChatHistory history, string text) => new()
    {
        ["Usage"] = new Dictionary<string, object?>
        {
            ["InputTokens"] = history.Sum(m => (m.Content ?? string.Empty).Length) / 4,
            ["OutputTokens"] = text.Length / 4,
        },
    };

    [GeneratedRegex(@"\[1\] (?<file>[^,\n]+), page (?<page>\d+):\n(?<text>[^\n]*)")]
    private static partial Regex FirstSource();

    private static string Respond(ChatHistory history)
    {
        var system = history.FirstOrDefault(m => m.Role == AuthorRole.System)?.Content ?? string.Empty;
        var user = history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? string.Empty;
        if (system.Contains("extract fields from an invoice", StringComparison.OrdinalIgnoreCase))
        {
            var fields = DocAssistant.Core.Extraction.RegexInvoiceExtractor.Extract(user);
            return System.Text.Json.JsonSerializer.Serialize(fields);
        }
        if (system.Contains("(no sources found)", StringComparison.Ordinal))
        {
            return "The documents do not contain information about that.";
        }
        var m = FirstSource().Match(system);
        if (!m.Success)
        {
            return "I could not find a source to answer that.";
        }
        var line = m.Groups["text"].Value.Trim();
        return $"According to {m.Groups["file"].Value} (page {m.Groups["page"].Value}): {line} [1]";
    }
}

/// <summary>
/// Deterministic bag-of-words embedder (hashed tokens, L2-normalised). Equal texts get equal vectors and
/// overlapping vocabularies get similar ones, which is all the tests need. No semantics are implied.
/// </summary>
public sealed class HashEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public static float[] Embed(string text, int dimensions)
    {
        var v = new float[dimensions];
        foreach (var token in Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+"))
        {
            if (token.Length < 2)
            {
                continue;
            }
            var h = Fnv1a(token);
            var idx = (int)(h % (uint)dimensions);
            var sign = ((h >> 31) & 1) == 0 ? 1f : -1f;
            v[idx] += sign;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }
        return v;
    }

    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.Select(t => new Embedding<float>(Embed(t, dimensions))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
