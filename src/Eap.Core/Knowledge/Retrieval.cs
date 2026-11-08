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
}
