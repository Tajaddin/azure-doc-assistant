using System.ClientModel;
using Azure.Identity;
using DocAssistant.Core.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;

namespace DocAssistant.Infrastructure.Ai;

/// <summary>Builds the Semantic Kernel for the configured provider and exposes the matching embedder.</summary>
public static class KernelFactory
{
    public static Kernel Create(AiOptions options, IServiceProvider? services = null)
    {
        var builder = Kernel.CreateBuilder();
        switch (options.Provider)
        {
            case "azure":
                if (string.IsNullOrWhiteSpace(options.Azure.Endpoint))
                {
                    throw new InvalidOperationException("Ai:Azure:Endpoint is required for the azure provider");
                }
                if (!string.IsNullOrWhiteSpace(options.Azure.ApiKey))
                {
                    builder.AddAzureOpenAIChatCompletion(options.Azure.ChatDeployment, options.Azure.Endpoint, options.Azure.ApiKey);
                    builder.AddAzureOpenAIEmbeddingGenerator(options.Azure.EmbeddingDeployment, options.Azure.Endpoint, options.Azure.ApiKey, dimensions: options.Azure.EmbeddingDimensions);
                }
                else
                {
                    var credential = new DefaultAzureCredential();
                    builder.AddAzureOpenAIChatCompletion(options.Azure.ChatDeployment, options.Azure.Endpoint, credential);
                    builder.AddAzureOpenAIEmbeddingGenerator(options.Azure.EmbeddingDeployment, options.Azure.Endpoint, credential, dimensions: options.Azure.EmbeddingDimensions);
                }
                break;
            case "ollama":
                // Ollama speaks the OpenAI wire protocol; the same connector that talks to OpenAI talks to it.
                var client = new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = new Uri(options.Ollama.Endpoint) });
                builder.AddOpenAIChatCompletion(options.Ollama.ChatModel, client);
                builder.AddOpenAIEmbeddingGenerator(options.Ollama.EmbeddingModel, client, dimensions: null);
                break;
            case "fake":
                builder.Services.AddSingleton<IChatCompletionService>(new FakeChatCompletionService());
                builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashEmbeddingGenerator(options.FakeEmbeddingDimensions));
                break;
            default:
                throw new InvalidOperationException($"unknown Ai:Provider {options.Provider}");
        }
        return builder.Build();
    }

    public static IEmbedder CreateEmbedder(Kernel kernel, AiOptions options)
    {
        var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var model = options.Provider switch
        {
            "azure" => options.Azure.EmbeddingDeployment,
            "ollama" => options.Ollama.EmbeddingModel,
            _ => "hash",
        };
        return new SkEmbedder(generator, model, options.EmbeddingDimensions);
    }
}

/// <summary>IEmbedder over Microsoft.Extensions.AI's generator, which is what Semantic Kernel registers.</summary>
public sealed class SkEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator, string model, int dimensions) : IEmbedder
{
    public string Model => model;
    public int Dimensions => dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
        {
            return [];
        }
        var result = await generator.GenerateAsync(texts, cancellationToken: ct);
        var vectors = result.Select(e => e.Vector.ToArray()).ToList();
        if (vectors.Any(v => v.Length != dimensions))
        {
            throw new InvalidOperationException($"embedder {model} returned {vectors[0].Length} dimensions, configured {dimensions}");
        }
        return vectors;
    }
}
