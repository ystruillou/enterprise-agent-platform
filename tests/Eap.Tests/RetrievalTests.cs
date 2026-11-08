using Eap.Core.Agents;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Eap.Evals;
using Microsoft.Extensions.AI;

namespace Eap.Tests;

public class RetrievalTests
{
    private static KnowledgeBase SavKnowledge()
    {
        var sav = AgentRegistry.LoadFrom(CoreTests.AgentsRoot).Find("sav-produit")!;
        return KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
    }

    [Fact]
    public async Task Semantic_search_with_fake_embeddings_finds_the_matching_section()
    {
        var retriever = new SemanticRetriever(SavKnowledge(), new FakeEmbeddingGenerator("fake"), RetrievalMode.Semantic);
        var result = await retriever.SearchAsync("Quel est le délai de livraison standard ?");
        Assert.Equal("Livraison", result.Chunks[0].Title);
        Assert.False(result.Degraded);
    }

    [Fact]
    public async Task Similarity_threshold_leaves_out_of_scope_questions_without_context()
    {
        var retriever = new SemanticRetriever(SavKnowledge(), new FakeEmbeddingGenerator("fake"), RetrievalMode.Semantic, minSimilarity: 0.3);
        Assert.Empty((await retriever.SearchAsync("Donne-moi une recette de crêpes")).Hits);
    }

    [Fact]
    public async Task Embedding_failure_falls_back_to_keyword_search_and_says_so()
    {
        var retriever = new SemanticRetriever(SavKnowledge(), new FailingEmbeddingGenerator(), RetrievalMode.Semantic);
        var result = await retriever.SearchAsync("Combien coûte la bouilloire inox ?");
        Assert.True(result.Degraded);
        Assert.StartsWith("Bouilloire", result.Chunks[0].Title);
    }

    [Fact]
    public async Task Documents_are_embedded_once_and_repeated_questions_hit_the_cache()
    {
        var counting = new CountingEmbeddingGenerator(new FakeEmbeddingGenerator("fake"));
        var retriever = new SemanticRetriever(SavKnowledge(), counting, RetrievalMode.Semantic);

        await retriever.SearchAsync("Quel est le délai de livraison ?");
        await retriever.SearchAsync("Quel est le délai de livraison ?");
        await retriever.SearchAsync("Combien coûte la bouilloire ?");

        Assert.Equal(3, counting.Calls); // 1 lot pour l'index + 2 questions distinctes
    }

    [Fact]
    public void Reciprocal_rank_fusion_favours_sections_ranked_by_both_methods()
    {
        KnowledgeChunk C(string t) => new("doc.md", t, "");
        var a = C("A"); var b = C("B"); var c = C("C");
        var fused = SemanticRetriever.Fuse(
            [new(a, 0.9), new(b, 0.8)],   // sémantique
            [new(b, 3), new(c, 2)])       // mots-clés
            .Select(h => h.Chunk.Title).ToList();
        Assert.Equal(["B", "A", "C"], fused);
    }

    [Fact]
    public void Cosine_similarity_is_one_for_colinear_vectors_and_zero_for_orthogonal()
    {
        Assert.Equal(1.0, SemanticRetriever.CosineSimilarity([1f, 2f], [2f, 4f]), 6);
        Assert.Equal(0.0, SemanticRetriever.CosineSimilarity([1f, 0f], [0f, 3f]), 6);
    }

    [Fact]
    public void Retrieval_settings_are_validated()
    {
        var m = new AgentManifest
        {
            Id = "x", Name = "x", Purpose = "p", Owners = new() { Business = "b", Tech = "t" },
            Retrieval = new() { Mode = RetrievalMode.Semantic, EmbeddingModel = "", Top = 0, MinSimilarity = 1.2 },
        };
        var errors = AgentRegistry.Validate(m).ToList();
        Assert.Contains(errors, e => e.Contains("retrieval.top"));
        Assert.Contains(errors, e => e.Contains("retrieval.min_similarity"));
        Assert.Contains(errors, e => e.Contains("embedding_model"));
    }

    [Fact]
    public void Expected_context_accepts_title_prefixes_and_alternatives()
    {
        var c = new Expectations { Context = ["Aspirateur", "Cafetière|Garantie"] };
        Assert.True(c.ContextFound(["Aspirateur sans fil Kerlenn Cyclone (réf. AS-CY2)", "Garantie"]));
        Assert.False(c.ContextFound(["Aspirateur sans fil Kerlenn Cyclone (réf. AS-CY2)", "Livraison"]));
        Assert.Null(new Expectations().ContextFound(["Livraison"]));
    }

    [Fact]
    public void Every_expected_context_in_the_dataset_names_a_real_section()
    {
        var titles = SavKnowledge().Chunks.Select(c => c.Title).ToList();
        var dataset = Path.Combine(Path.GetDirectoryName(CoreTests.AgentsRoot)!, "evals", "sav-produit.jsonl");
        foreach (var c in EvalCase.LoadJsonl(dataset))
            foreach (var label in c.Expect.Context)
                Assert.True(new Expectations { Context = [label] }.ContextFound(titles) == true,
                    $"{c.Id} : « {label} » ne correspond à aucune section");
    }

    private sealed class FailingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("quota dépassé");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CountingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.GenerateAsync(values, options, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
