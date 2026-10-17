namespace DocAssistant.Infrastructure.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>ollama (local, free), azure (Azure OpenAI), fake (deterministic, tests and CI).</summary>
    public string Provider { get; set; } = "ollama";

    public OllamaOptions Ollama { get; set; } = new();
    public AzureOpenAIOptions Azure { get; set; } = new();

    public string ChatModelName => Provider switch
    {
        "azure" => Azure.ChatDeployment,
        "ollama" => Ollama.ChatModel,
        _ => "fake",
    };

    public int EmbeddingDimensions => Provider switch
    {
        "azure" => Azure.EmbeddingDimensions,
        "ollama" => Ollama.EmbeddingDimensions,
        _ => FakeEmbeddingDimensions,
    };

    public int FakeEmbeddingDimensions { get; set; } = 256;
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string ChatModel { get; set; } = "qwen2.5:3b";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int EmbeddingDimensions { get; set; } = 768;
}

public sealed class AzureOpenAIOptions
{
    public string Endpoint { get; set; } = "";
    /// <summary>Optional. When empty, DefaultAzureCredential (managed identity in Azure, az login locally) is used.</summary>
    public string ApiKey { get; set; } = "";
    public string ChatDeployment { get; set; } = "gpt-4o-mini";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; set; } = 1536;
}
