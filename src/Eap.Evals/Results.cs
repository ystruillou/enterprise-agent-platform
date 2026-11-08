using System.Text;
using Eap.Core.Agents;
using Eap.Core.FinOps;

namespace Eap.Evals;

public sealed record CaseResult(
    EvalCase Case, int Run, string Answer, IReadOnlyList<CheckResult> Checks,
    JudgeVerdict? Judge, UsageRecord? Usage, string? Error, bool? ContextFound = null, int GuardrailActions = 0,
    string? ModelAnswer = null)
{
    private bool JudgeApproves => Judge?.Score >= LlmJudge.PassScore;

    /// <summary>
    /// Un cas réussit si tous les contrôles passent et, si un juge est utilisé, s'il donne au moins 4/5.
    /// Exception : un contrôle arbitrable (fragment suspect) en échec est levé quand le juge valide la réponse.
    /// Sans verdict du juge (absent, indisponible, illisible), il reste en échec : le doute ne profite pas au modèle.
    /// </summary>
    public bool Passed => Error is null
        && Checks.All(c => c.Passed || (c.Arbitrable && JudgeApproves))
        && (Judge?.Score is null || JudgeApproves);

    /// <summary>Contrôles en échec levés par le juge : à relire, car c'est là qu'un juge trop indulgent se verrait.</summary>
    public IReadOnlyList<CheckResult> Arbitrated =>
        JudgeApproves ? Checks.Where(c => !c.Passed && c.Arbitrable).ToList() : [];

    public IEnumerable<string> FailureReasons =>
        Error is not null ? [Error]
        : Checks.Where(c => !c.Passed && !(c.Arbitrable && JudgeApproves))
            .Select(c => (c.Detail is null ? c.Name : $"{c.Name} : {c.Detail}") + (c.Arbitrable && Judge is null ? " (aucun juge pour arbitrer)" : ""))
            .Concat(Judge?.Score is < LlmJudge.PassScore ? [$"juge {Judge.Score}/5 : {Judge.Reason}"] : []);
}

public sealed record EvalSummary(
    AgentManifest Agent, string Provider, string Model, string? ServedModel, string? JudgeLabel,
    string Retrieval, IReadOnlyList<CaseResult> Results, DateTimeOffset At)
{
    /// <summary>Réponses corrigées par un garde-fou : le score mesure ce que voit l'utilisateur, ce compteur montre le modèle brut.</summary>
    public int GuardrailInterventions => Results.Count(r => r.GuardrailActions > 0);

    /// <summary>Cas sauvés par le juge (contrôle par mots-clés levé) : le score sans juge serait plus bas d'autant.</summary>
    public int JudgeArbitrations => Results.Count(r => r.Arbitrated.Count > 0);

    /// <summary>Réponses que les contrôles valident mais que le juge refuse : ce que les mots-clés ne voient pas.</summary>
    public int JudgeRejections => Results.Count(r => r.Error is null && r.Checks.All(c => c.Passed) && r.Judge?.Score < LlmJudge.PassScore);

    /// <summary>Appels au juge sans note exploitable (indisponible, réponse illisible).</summary>
    public int JudgeMissing => JudgeLabel is null ? 0 : Results.Count(r => r.Error is null && r.Judge?.Score is null);

    /// <summary>Cas dont la recherche a retrouvé les sections attendues, parmi ceux qui en déclarent.</summary>
    public (int Found, int Labeled) ContextRecall =>
        (Results.Count(r => r.ContextFound == true), Results.Count(r => r.ContextFound is not null));
    public int Total => Results.Count;
    public int Passed => Results.Count(r => r.Passed);
    public double Score => Total == 0 ? 0 : (double)Passed / Total;
    public bool MeetsThreshold => Score >= Agent.Evaluation.MinScore;
    public decimal AgentCostUsd => Results.Sum(r => r.Usage?.CostUsd ?? 0m);
    public decimal JudgeCostUsd => Results.Sum(r => r.Judge?.CostUsd ?? 0m);
    public double? AverageJudgeScore => Results.Where(r => r.Judge?.Score is not null).Select(r => (double)r.Judge!.Score!.Value).DefaultIfEmpty().Average() is var a && a > 0 ? a : null;
    public double MedianLatencyMs
    {
        get
        {
            var l = Results.Where(r => r.Usage is not null).Select(r => r.Usage!.LatencyMs).Order().ToList();
            return l.Count == 0 ? 0 : l[l.Count / 2];
        }
    }

    public static EvalSummary From(AgentManifest agent, string provider, string model, string? judge, string retrieval, IReadOnlyList<CaseResult> results) =>
        new(agent, provider, model, results.Select(r => r.Usage?.ServedModel).FirstOrDefault(m => m is not null), judge, retrieval, results, DateTimeOffset.Now);
}

public static class ReportWriter
{
    public static string Write(string directory, EvalSummary s)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{s.Agent.Id}_{s.Provider}_{s.Model}_{s.Retrieval}_{s.At:yyyyMMdd-HHmm}.md");
        File.WriteAllText(path, Render(s), new UTF8Encoding(false));
        return path;
    }

    public static string Render(EvalSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Évaluation : {s.Agent.Name} (v{s.Agent.Version})").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        sb.AppendLine($"| Date | {s.At:yyyy-MM-dd HH:mm} |");
        sb.AppendLine($"| Modèle | `{s.Provider}/{s.Model}`{(s.ServedModel is { } sm && sm != s.Model ? $" (servi : `{sm}`)" : "")} |");
        sb.AppendLine($"| Recherche documentaire | {s.Retrieval} · sections attendues retrouvées : {s.ContextRecall.Found}/{s.ContextRecall.Labeled} |");
        sb.AppendLine($"| Garde-fou des sources | {(s.Agent.Guardrails.VerifySources ? $"actif · {s.GuardrailInterventions} réponse(s) corrigée(s)" : "désactivé (modèle brut)")} |");
        sb.AppendLine($"| Juge | {(s.JudgeLabel is null ? "aucun (contrôles déterministes seuls)" : $"`{s.JudgeLabel}` · {s.JudgeArbitrations} contrôle(s) levé(s) · {s.JudgeRejections} refus que les contrôles n'ont pas vus{(s.JudgeMissing > 0 ? $" · {s.JudgeMissing} sans verdict" : "")}")} |");
        sb.AppendLine($"| **Score** | **{s.Score:P0}** ({s.Passed}/{s.Total}) |");
        sb.AppendLine($"| Seuil de passage en production | {s.Agent.Evaluation.MinScore:P0} |");
        sb.AppendLine($"| **Verdict** | **{(s.MeetsThreshold ? "✅ VALIDÉ" : "❌ REFUSÉ")}** |");
        if (s.AverageJudgeScore is { } avg) sb.AppendLine($"| Note moyenne du juge | {avg:0.0}/5 |");
        sb.AppendLine($"| Latence médiane | {s.MedianLatencyMs:0} ms |");
        sb.AppendLine($"| Coût | agent {s.AgentCostUsd:0.######} $ · juge {s.JudgeCostUsd:0.######} $ |").AppendLine();

        sb.AppendLine("## Résultats par catégorie").AppendLine();
        sb.AppendLine("| Catégorie | Réussis | Taux |").AppendLine("|---|---|---|");
        foreach (var g in s.Results.GroupBy(r => r.Case.Category).OrderBy(g => g.Key))
            sb.AppendLine($"| {g.Key} | {g.Count(r => r.Passed)}/{g.Count()} | {(double)g.Count(r => r.Passed) / g.Count():P0} |");
        sb.AppendLine();

        var failures = s.Results.Where(r => !r.Passed).ToList();
        sb.AppendLine($"## Échecs ({failures.Count})").AppendLine();
        if (failures.Count == 0) sb.AppendLine("Aucun.").AppendLine();
        foreach (var f in failures)
        {
            sb.AppendLine($"### {f.Case.Id} ({f.Case.Category}){(f.Run > 1 ? $", essai {f.Run}" : "")}").AppendLine();
            sb.AppendLine($"**Question :** {f.Case.Question}").AppendLine();
            foreach (var reason in f.FailureReasons) sb.AppendLine($"- ❌ {reason}");
            sb.AppendLine().AppendLine("<details><summary>Réponse</summary>").AppendLine();
            sb.AppendLine("```text").AppendLine(f.Answer.Trim()).AppendLine("```").AppendLine("</details>").AppendLine();
        }

        var guarded = s.Results.Where(r => r.GuardrailActions > 0).ToList();
        if (guarded.Count > 0)
        {
            // Relire ces citations : une source réelle remplacée à tort serait un faux positif du garde-fou.
            sb.AppendLine($"## Interventions du garde-fou ({guarded.Count})").AppendLine();
            sb.AppendLine("| Cas | Citation d'origine du modèle (remplacée) |").AppendLine("|---|---|");
            foreach (var r in guarded)
                sb.AppendLine($"| {r.Case.Id}{(r.Run > 1 ? $" (essai {r.Run})" : "")} | {(r.ModelAnswer is { } m ? Eap.Core.Guardrails.SourceLine.Extract(m) : null)?.Replace("|", "/") ?? "?"} |");
            sb.AppendLine();
        }

        // Décisions du juge qui changent le verdict des contrôles, dans un sens comme dans l'autre : à relire par un humain.
        var arbitrated = s.Results.Where(r => r.Arbitrated.Count > 0).ToList();
        if (arbitrated.Count > 0)
        {
            sb.AppendLine($"## Contrôles levés par le juge ({arbitrated.Count})").AppendLine();
            sb.AppendLine("| Cas | Fragment suspect présent | Note | Raison du juge |").AppendLine("|---|---|---|---|");
            foreach (var r in arbitrated)
                sb.AppendLine($"| {r.Case.Id}{(r.Run > 1 ? $" (essai {r.Run})" : "")} | {string.Join(", ", r.Arbitrated.Select(c => c.Detail))} | " +
                              $"{r.Judge!.Score}/5 | {r.Judge.Reason.Replace("|", "/")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Détail des cas").AppendLine();
        sb.AppendLine("| Cas | Catégorie | Résultat | Contexte | Juge | Tokens | Latence |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in s.Results)
            sb.AppendLine($"| {r.Case.Id} | {r.Case.Category} | {(r.Passed ? "✅" : "❌")} | {r.ContextFound switch { true => "✅", false => "❌", null => "–" }} | {(r.Judge?.Score is { } sc ? $"{sc}/5" : "–")} | " +
                          $"{(r.Usage is null ? "–" : $"{r.Usage.InputTokens}+{r.Usage.OutputTokens}")} | {(r.Usage is null ? "–" : $"{r.Usage.LatencyMs:0} ms")} |");
        return sb.ToString();
    }
}
