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

    public IChatClient Create(string provider, string model) =>
        _cache.GetOrAdd((provider.ToLowerInvariant(), model), key => Build(key.Item1, key.Item2));

    public bool SupportsTemperature(string provider) =>
        !providers.TryGetValue(provider, out var p) || p.SupportsTemperature;

    public ModelPrice? PriceOf(string provider, string model) =>
        providers.TryGetValue(provider, out var p) && p.Prices.TryGetValue(model, out var price) ? price : null;

    private IChatClient Build(string provider, string model)
    {
        if (!providers.TryGetValue(provider, out var options))
            throw new InvalidOperationException($"Fournisseur inconnu : {provider}");

        IChatClient inner;
        if (clientOverride is not null)
        {
            inner = clientOverride; // tests uniquement
        }
        else if (provider == "fake")
        {
            inner = new FakeChatClient(model);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKeyEnv))
                throw new InvalidOperationException($"Fournisseur {provider} : Endpoint et ApiKeyEnv sont obligatoires");

            var apiKey = _readEnv(options.ApiKeyEnv)
                ?? throw new InvalidOperationException(
                    $"Clé API absente : définissez la variable d'environnement {options.ApiKeyEnv}");

            inner = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) })
                .GetChatClient(model)
                .AsIChatClient();
        }

        return new ChatClientBuilder(inner)
            // Par défaut on ne journalise PAS le contenu des prompts/réponses dans les traces (RGPD).
            .UseOpenTelemetry(sourceName: TelemetrySource, configure: c => c.EnableSensitiveData = false)
            .Build();
    }
}
