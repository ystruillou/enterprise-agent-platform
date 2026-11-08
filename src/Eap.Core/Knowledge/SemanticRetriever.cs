using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Eap.Core.Knowledge;

/// <summary>
/// Recherche sémantique (et hybride) sur la base de connaissances d'un agent.
/// <para>
/// Les extraits sont convertis en vecteurs une seule fois (au premier appel), puis chaque question est vectorisée
/// et comparée par similarité cosinus. L'index tient en mémoire : suffisant pour quelques centaines d'extraits ;
/// au-delà, on passe à une base vectorielle (pgvector, Vertex AI Vector Search…) derrière la même interface.
/// </para>
/// <para>
/// Résilience : si le fournisseur d'embeddings échoue, on se replie sur la recherche par mots-clés
/// (<see cref="RetrievalResult.Degraded"/>) au lieu de faire échouer la conversation.
/// </para>
/// </summary>
public sealed class SemanticRetriever(
    KnowledgeBase knowledge,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    RetrievalMode mode,
    int top = 3,
    double minSimilarity = 0,
    int? dimensions = null) : IKnowledgeRetriever
{
    /// <summary>Constante de la Reciprocal Rank Fusion (valeur usuelle de la littérature).</summary>
    private const int RrfK = 60;
    private const int MaxBatch = 100;

    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private float[][]? _vectors;
    private const int MaxCachedQueries = 1000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> _queryCache = new();

    public RetrievalMode Mode { get; } = mode is RetrievalMode.Keyword
        ? throw new ArgumentException("SemanticRetriever gère les modes Semantic et Hybrid ; utilisez KnowledgeBase pour Keyword.")
        : mode;

    public async Task<RetrievalResult> SearchAsync(string query, CancellationToken ct = default)
    {
        if (knowledge.Count == 0 || string.IsNullOrWhiteSpace(query))
            return new RetrievalResult([], Mode);

        IReadOnlyList<RetrievalHit> semantic;
        try
        {
            semantic = await SemanticHitsAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.AddEvent(new ActivityEvent("eap.retrieval.fallback",
                tags: new ActivityTagsCollection { ["exception.type"] = ex.GetType().Name }));
            return new RetrievalResult(knowledge.SearchWithScores(query, top), Mode, Degraded: true);
        }

        if (Mode == RetrievalMode.Semantic)
            return new RetrievalResult(semantic.Take(top).ToList(), Mode);

        var keyword = knowledge.SearchWithScores(query, knowledge.Count);
        return new RetrievalResult(Fuse(semantic, keyword).Take(top).ToList(), Mode);
    }

    /// <summary>Tous les extraits au-dessus du seuil de similarité, du plus proche au plus lointain.</summary>
    public async Task<IReadOnlyList<RetrievalHit>> SemanticHitsAsync(string query, CancellationToken ct = default) =>
        (await RankAsync(query, ct)).Where(h => h.Score >= minSimilarity).ToList();

    /// <summary>Tous les extraits classés par similarité, sans seuil (utile pour calibrer <c>min_similarity</c>).</summary>
    public async Task<IReadOnlyList<RetrievalHit>> RankAsync(string query, CancellationToken ct = default)
    {
        var vectors = await EnsureIndexAsync(ct);
        var q = await QueryVectorAsync(query, ct);
        return knowledge.Chunks
            .Select((c, i) => new RetrievalHit(c, CosineSimilarity(q, vectors[i])))
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    // Les mêmes questions reviennent souvent (FAQ) : le cache évite de repayer l'embedding et ménage le quota.
    // Taille bornée ; vidé d'un bloc quand il est plein (suffisant ici, un vrai LRU ou Redis en production).
    private async Task<float[]> QueryVectorAsync(string query, CancellationToken ct)
    {
        var key = query.Trim();
        if (_queryCache.TryGetValue(key, out var cached)) return cached;
        var vector = (await embeddings.GenerateAsync([key], Options(), ct))[0].Vector.ToArray();
        if (_queryCache.Count >= MaxCachedQueries) _queryCache.Clear();
        _queryCache[key] = vector;
        return vector;
    }

    /// <summary>
    /// Reciprocal Rank Fusion : chaque liste vote 1 / (k + rang). On combine ainsi des scores incomparables
    /// (cosinus d'un côté, nombre de mots communs de l'autre) sans avoir à les calibrer.
    /// </summary>
    public static IEnumerable<RetrievalHit> Fuse(params IReadOnlyList<RetrievalHit>[] rankings) =>
        rankings
            .SelectMany(r => r.Select((h, rank) => (h.Chunk, Vote: 1.0 / (RrfK + rank + 1))))
            .GroupBy(x => x.Chunk)
            .Select(g => new RetrievalHit(g.Key, g.Sum(x => x.Vote)))
            .OrderByDescending(h => h.Score);

    private async Task<float[][]> EnsureIndexAsync(CancellationToken ct)
    {
        if (_vectors is { } ready) return ready;
        await _indexLock.WaitAsync(ct);
        try
        {
            if (_vectors is { } built) return built;
            // Le titre est inclus : « Retours et remboursement » porte une bonne partie du sens de la section.
            var texts = knowledge.Chunks.Select(c => $"{c.Title}\n{c.Content}").ToList();
            var vectors = new List<float[]>(texts.Count);
            foreach (var batch in texts.Chunk(MaxBatch))
                vectors.AddRange((await embeddings.GenerateAsync(batch, Options(), ct)).Select(e => e.Vector.ToArray()));
            // Un échec ne met rien en cache : la prochaine question retentera la construction de l'index.
            return _vectors = vectors.ToArray();
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private EmbeddingGenerationOptions? Options() =>
        dimensions is { } d ? new EmbeddingGenerationOptions { Dimensions = d } : null;

    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vecteurs de dimensions différentes.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
