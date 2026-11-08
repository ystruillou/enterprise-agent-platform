using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Eap.Core.Models;

/// <summary>
/// Fabrique de clients de chat : un agent désigne un fournisseur et un modèle,
/// la fabrique renvoie un <see cref="IChatClient"/> instrumenté (OpenTelemetry, conventions gen_ai.*).
/// </summary>
public sealed class ChatClientFactory(
    IReadOnlyDictionary<string, ProviderOptions> providers,
    Func<string, string?>? readEnvironment = null,
    IChatClient? clientOverride = null)
{
    public const string TelemetrySource = "Eap.GenAI";

    private readonly Func<string, string?> _readEnv = readEnvironment ?? Environment.GetEnvironmentVariable;
    private readonly ConcurrentDictionary<(string, string), IChatClient> _cache = new();
    private readonly ConcurrentDictionary<(string, string), IEmbeddingGenerator<string, Embedding<float>>> _embeddingCache = new();

    public IChatClient Create(string provider, string model) =>
        _cache.GetOrAdd((provider.ToLowerInvariant(), model), key => Build(key.Item1, key.Item2));

    public bool SupportsTemperature(string provider) =>
        !providers.TryGetValue(provider, out var p) || p.SupportsTemperature;

    public ModelPrice? PriceOf(string provider, string model) =>
        providers.TryGetValue(provider, out var p) && p.Prices.TryGetValue(model, out var price) ? price : null;

    /// <summary>
    /// Générateur d'embeddings pour la recherche sémantique. Le fournisseur peut différer de celui du chat :
    /// Anthropic ne propose pas d'embeddings, un agent Claude utilise donc ceux de Gemini (ou d'OpenAI).
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string provider, string model) =>
        _embeddingCache.GetOrAdd((provider.ToLowerInvariant(), model), key =>
        {
            var inner = key.Item1 == "fake"
                ? new FakeEmbeddingGenerator(key.Item2)
                : OpenAIClientFor(key.Item1).GetEmbeddingClient(key.Item2).AsIEmbeddingGenerator();
            return new EmbeddingGeneratorBuilder<string, Embedding<float>>(inner)
                .UseOpenTelemetry(sourceName: TelemetrySource, configure: c => c.EnableSensitiveData = false)
                .Build();
        });

    private OpenAIClient OpenAIClientFor(string provider)
    {
        if (!providers.TryGetValue(provider, out var options))
            throw new InvalidOperationException($"Fournisseur inconnu : {provider}");
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKeyEnv))
            throw new InvalidOperationException($"Fournisseur {provider} : Endpoint et ApiKeyEnv sont obligatoires");

        var apiKey = _readEnv(options.ApiKeyEnv)
            ?? throw new InvalidOperationException(
                $"Clé API absente : définissez la variable d'environnement {options.ApiKeyEnv}");

        return new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
    }

    private IChatClient Build(string provider, string model)
    {
        if (!providers.ContainsKey(provider))
            throw new InvalidOperationException($"Fournisseur inconnu : {provider}");

        IChatClient inner;
        if (clientOverride is not null)
            inner = clientOverride; // tests uniquement
        else if (provider == "fake")
            inner = new FakeChatClient(model);
        else
            inner = OpenAIClientFor(provider).GetChatClient(model).AsIChatClient();

        return new ChatClientBuilder(inner)
            // Par défaut on ne journalise PAS le contenu des prompts/réponses dans les traces (RGPD).
            .UseOpenTelemetry(sourceName: TelemetrySource, configure: c => c.EnableSensitiveData = false)
            .Build();
    }
}
