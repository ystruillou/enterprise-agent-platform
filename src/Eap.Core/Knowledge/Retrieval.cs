namespace Eap.Core.Knowledge;

/// <summary>Extrait retenu par la recherche, avec son score (recouvrement de mots-clés, similarité cosinus ou score RRF).</summary>
public sealed record RetrievalHit(KnowledgeChunk Chunk, double Score);

/// <summary>
/// Résultat d'une recherche documentaire. <see cref="Degraded"/> signale un repli sur la recherche par mots-clés
/// (fournisseur d'embeddings indisponible) : l'agent continue de répondre, avec une qualité moindre, et la trace le dit.
/// </summary>
public sealed record RetrievalResult(IReadOnlyList<RetrievalHit> Hits, RetrievalMode Mode, bool Degraded = false)
{
    public IReadOnlyList<KnowledgeChunk> Chunks => Hits.Select(h => h.Chunk).ToList();
}

/// <summary>
/// Recherche du contexte documentaire d'un agent. Toutes les stratégies (mots-clés, sémantique, hybride)
/// partagent ce contrat, ce qui permet de les comparer sur le même jeu d'évaluation.
/// </summary>
public interface IKnowledgeRetriever
{
    Task<RetrievalResult> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>Toutes les sections de la base : sert au garde-fou qui vérifie les sources citées.</summary>
    IReadOnlyList<KnowledgeChunk> Corpus { get; }
}

public enum RetrievalMode
{
    /// <summary>Recouvrement de mots-clés : gratuit, déterministe, mais aveugle aux synonymes (« livré » ≠ « livraison »).</summary>
    Keyword,
    /// <summary>Similarité de sens entre embeddings : trouve les reformulations, coûte un appel d'embedding par question.</summary>
    Semantic,
    /// <summary>Fusion des deux classements (Reciprocal Rank Fusion) : robuste aux synonymes ET aux termes exacts (références produit).</summary>
    Hybrid,
    /// <summary>
    /// Toute la base à chaque appel, sans recherche : pour un référentiel court qui doit être appliqué en entier
    /// (un relecteur de conformité vérifie toutes les règles, pas seulement celles qui ressemblent au texte).
    /// </summary>
    All,
}

/// <summary>Renvoie toute la base. Plafonné : au-delà, le coût par appel et la dilution du contexte imposent une vraie recherche.</summary>
public sealed class FullKnowledgeRetriever : IKnowledgeRetriever
{
    public const int MaxSections = 30;
    private readonly RetrievalResult _all;

    public FullKnowledgeRetriever(KnowledgeBase knowledge)
    {
        if (knowledge.Chunks.Count > MaxSections)
            throw new InvalidOperationException(
                $"retrieval.mode all est réservé aux bases courtes ({knowledge.Chunks.Count} sections, maximum {MaxSections}) : utiliser hybrid.");
        Corpus = knowledge.Chunks;
        _all = new RetrievalResult(knowledge.Chunks.Select(c => new RetrievalHit(c, 1)).ToList(), RetrievalMode.All);
    }

    public IReadOnlyList<KnowledgeChunk> Corpus { get; }

    public Task<RetrievalResult> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(_all);
}
