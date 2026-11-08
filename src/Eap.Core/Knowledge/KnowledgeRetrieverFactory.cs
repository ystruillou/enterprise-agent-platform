using System.Collections.Concurrent;
using Eap.Core.Agents;
using Eap.Core.Models;

namespace Eap.Core.Knowledge;

/// <summary>
/// Construit (et garde en cache) la recherche documentaire de chaque agent selon sa fiche.
/// </summary>
/// <param name="embeddingProviderOverride">Force un fournisseur d'embeddings ("fake" en dev/CI, sans clé).</param>
/// <param name="modeOverride">Force une stratégie pour tous les agents (comparaisons en évaluation).</param>
public sealed class KnowledgeRetrieverFactory(
    ChatClientFactory clients,
    string? embeddingProviderOverride = null,
    RetrievalMode? modeOverride = null)
{
    private readonly ConcurrentDictionary<string, IKnowledgeRetriever> _cache = new();

    public IKnowledgeRetriever For(AgentManifest agent) => _cache.GetOrAdd(agent.Id, _ => Build(agent));

    private IKnowledgeRetriever Build(AgentManifest agent)
    {
        var knowledge = agent.KnowledgeDir is { Length: > 0 } dir
            ? KnowledgeBase.LoadFrom(Path.Combine(agent.BaseDirectory, dir))
            : KnowledgeBase.Empty;
        var r = agent.Retrieval;
        var mode = modeOverride ?? r.Mode;
        if (mode == RetrievalMode.Keyword) return knowledge;

        var provider = embeddingProviderOverride ?? r.EmbeddingProvider;
        // Le seuil est calibré pour le modèle d'embeddings de la fiche : les similarités d'un autre modèle
        // n'ont pas la même échelle (0,50 chez Gemini ne veut rien dire pour le générateur simulé). Il ne s'applique donc pas.
        var minSimilarity = string.Equals(provider, r.EmbeddingProvider, StringComparison.OrdinalIgnoreCase) ? r.MinSimilarity : 0;
        return new SemanticRetriever(knowledge, clients.CreateEmbeddingGenerator(provider, r.EmbeddingModel),
            mode, r.Top, minSimilarity, r.Dimensions);
    }
}
