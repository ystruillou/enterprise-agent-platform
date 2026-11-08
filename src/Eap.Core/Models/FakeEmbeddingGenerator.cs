using Eap.Core.Knowledge;
using Microsoft.Extensions.AI;

namespace Eap.Core.Models;

/// <summary>
/// Générateur d'embeddings simulé, déterministe et gratuit (tests, CI, démo sans clé).
/// Chaque mot (normalisé comme pour la recherche par mots-clés) active une dimension choisie par hachage :
/// deux textes qui partagent des mots ont donc une similarité positive. Il ne comprend pas les synonymes,
/// ce qui est voulu : seul un vrai modèle d'embeddings apporte ce gain, et l'évaluation doit le montrer.
/// </summary>
public sealed class FakeEmbeddingGenerator(string modelId, int dimensions = 256) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var size = options?.Dimensions ?? dimensions;
        var result = new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(Vectorize(v, size)) { ModelId = modelId }));
        return Task.FromResult(result);
    }

    private static float[] Vectorize(string text, int size)
    {
        var vector = new float[size];
        // Présence plutôt que fréquence : les mots répétés gonflaient la norme des longues sections et les pénalisaient.
        foreach (var term in KnowledgeBase.Tokenize(text).Distinct())
            vector[StableHash(term) % (uint)size] = 1f;
        return vector;
    }

    // string.GetHashCode varie d'un processus à l'autre : un hachage stable rend les tests reproductibles.
    private static uint StableHash(string s)
    {
        var h = 2166136261u; // FNV-1a
        foreach (var c in s) h = (h ^ c) * 16777619u;
        return h;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
