using Eap.Core.Agents;
using Eap.Core.Knowledge;

namespace Eap.Evals;

/// <summary>
/// Évaluation de la recherche seule (option --retrieval-only) : aucun appel au modèle de chat, donc rapide et quasi gratuit.
/// Mesure le rappel (les sections attendues sont-elles transmises au modèle ?) et le bruit (du contexte envoyé pour une
/// question hors sujet), et affiche la similarité brute pour calibrer <c>retrieval.min_similarity</c>.
/// </summary>
public static class RetrievalReport
{
    public static async Task<int> RunAsync(
        AgentManifest agent, IReadOnlyList<EvalCase> cases, KnowledgeBase kb, IKnowledgeRetriever retriever,
        RetrievalMode mode)
    {
        // Similarité brute de la meilleure section, sans seuil (calibrage). Même instance : le vecteur de la question est en cache.
        var raw = retriever as SemanticRetriever;

        Console.WriteLine($"Recherche {mode} sur {agent.Id} ({kb.Count} sections, top {agent.Retrieval.Top}" +
                          (mode is RetrievalMode.Keyword or RetrievalMode.All ? ")" : $", seuil {agent.Retrieval.MinSimilarity:0.00}, {agent.Retrieval.EmbeddingModel})"));
        Console.WriteLine();

        var labeledTop = new List<double>();
        var unlabeledTop = new List<double>();
        int found = 0, labeled = 0, noisy = 0, unlabeled = 0;
        foreach (var c in cases)
        {
            var result = await retriever.SearchAsync(c.Question);
            var titles = result.Chunks.Select(h => h.Title).ToList();
            var ok = c.Expect.ContextFound(titles);
            double? best = raw is null ? null : (await raw.RankAsync(c.Question)).FirstOrDefault()?.Score;

            if (ok is null)
            {
                unlabeled++;
                if (titles.Count > 0) noisy++;
                if (best is { } b) unlabeledTop.Add(b);
            }
            else
            {
                labeled++;
                if (ok == true) found++;
                if (best is { } b) labeledTop.Add(b);
            }

            var mark = ok switch { true => "OK   ", false => "RATÉ ", null => "  -  " };
            var sim = best is { } s ? $" sim.max {s:0.000}" : "";
            var got = titles.Count == 0 ? "(aucune section)" : string.Join(" | ", result.Hits.Select(h => $"{Short(h.Chunk.Title)} {h.Score:0.###}"));
            Console.WriteLine($"  {mark}{c.Id,-8}{sim}  {got}{(result.Degraded ? "  [repli mots-clés]" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine($"Rappel : {found}/{labeled} questions reçoivent les sections attendues");
        Console.WriteLine($"Bruit  : {noisy}/{unlabeled} questions sans section attendue reçoivent quand même du contexte");
        if (labeledTop.Count > 0 && unlabeledTop.Count > 0)
            Console.WriteLine($"Calibrage : similarité max la plus faible parmi les questions documentées {labeledTop.Min():0.000}, " +
                              $"la plus forte parmi les autres {unlabeledTop.Max():0.000}");
        return 0;
    }

    private static string Short(string title) => title.Length <= 24 ? title : title[..24] + "…";
}
