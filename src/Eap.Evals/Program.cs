using System.Globalization;
using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Eap.Evals;
using Microsoft.Extensions.Configuration;

// Usage :
//   dotnet run --project src/Eap.Evals -- --agent sav-produit
//        [--provider anthropic --model claude-haiku-5-5]   force un modèle (sinon celui de la fiche)
//        [--judge gemini:gemini-3.6-flash]                  ajoute un modèle juge
//        [--retrieval keyword|semantic|hybrid]             force la stratégie de recherche (sinon celle de la fiche)
//        [--retrieval-only]                                 mesure la recherche seule, sans appeler le modèle de chat
//        [--repeat 2] [--delay-ms 1000] [--no-gate] [--report-dir evals/reports]
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var opt = EvalOptions.Parse(args);
var root = FindRepoRoot();

var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(root, "src", "Eap.Api", "appsettings.json"))
    .AddEnvironmentVariables()
    .Build();
var providers = new Dictionary<string, ProviderOptions>(
    config.GetSection("Providers").Get<Dictionary<string, ProviderOptions>>() ?? [], StringComparer.OrdinalIgnoreCase);

var agent = AgentRegistry.LoadFrom(Path.Combine(root, "agents")).Find(opt.Agent)
    ?? throw new ArgumentException($"Agent inconnu : {opt.Agent}");
if (string.IsNullOrWhiteSpace(agent.Evaluation.Dataset))
    throw new InvalidOperationException($"La fiche de {agent.Id} ne déclare pas de jeu d'évaluation.");

var cases = EvalCase.LoadJsonl(Path.GetFullPath(Path.Combine(agent.BaseDirectory, agent.Evaluation.Dataset)));
var kb = agent.KnowledgeDir is { Length: > 0 } dir ? KnowledgeBase.LoadFrom(Path.Combine(agent.BaseDirectory, dir)) : KnowledgeBase.Empty;
var known = new KnownSources(kb.Chunks.Select(c => c.Source), kb.Chunks.Select(c => c.Title));

var clients = new ChatClientFactory(providers);
var retrievalMode = opt.Retrieval ?? agent.Retrieval.Mode;
var retriever = new KnowledgeRetrieverFactory(clients, modeOverride: retrievalMode).For(agent);

if (opt.RetrievalOnly)
    return await RetrievalReport.RunAsync(agent, cases, kb, retriever, retrievalMode);

var modelOverride = opt.Provider is null ? null : new ModelOverride(opt.Provider, opt.Model ?? throw new ArgumentException("--model est requis avec --provider"));
var runner = new AgentRunner(clients, new InMemoryUsageLedger(), _ => retriever, modelOverride: modelOverride);

LlmJudge? judge = null;
if (opt.Judge is { } j)
{
    var parts = j.Split(':', 2);
    var referenceDocs = string.Join("\n\n", kb.Chunks.Select(c => $"[{c.Source} › {c.Title}]\n{c.Content.Trim()}"));
    judge = new LlmJudge(clients, parts[0], parts.Length > 1 ? parts[1] : throw new ArgumentException("--judge fournisseur:modèle"), referenceDocs, agent.SystemPrompt);
}

var provider = modelOverride?.Provider ?? agent.Model.Provider;
var model = modelOverride?.Model ?? agent.Model.Name;
Console.WriteLine($"Évaluation de {agent.Id} v{agent.Version} sur {provider}/{model}, recherche {retrievalMode} : {cases.Count} cas × {opt.Repeat}" +
                  (judge is null ? "" : $", juge {judge.Label}"));

var results = new List<CaseResult>();
foreach (var evalCase in cases)
for (var run = 1; run <= opt.Repeat; run++)
{
    CaseResult result;
    try
    {
        // Un utilisateur distinct par cas : les quotas par utilisateur ne doivent pas fausser la campagne.
        var reply = await WithRetry(() => runner.AskAsync(agent, $"eval-{evalCase.Id}-{run}", evalCase.Question));
        var checks = ResponseChecks.Run(evalCase, reply.Answer, known).ToList();
        checks.Add(new CheckResult("réponse complète", !reply.Truncated, reply.Truncated ? "tronquée : plafond de tokens atteint" : null));
        JudgeVerdict? verdict = null;
        if (judge is not null)
        {
            try { verdict = await WithRetry(() => judge.EvaluateAsync(evalCase.Question, reply.Answer)); }
            catch (ModelProviderException ex) { verdict = new JudgeVerdict(null, $"juge indisponible (HTTP {ex.HttpStatus})", 0m); }
        }
        var contextFound = evalCase.Expect.ContextFound(reply.Sources.Select(s => s.Split(" › ", 2)[^1]));
        result = new CaseResult(evalCase, run, reply.Answer, checks, verdict, reply.Usage, null, contextFound);
    }
    catch (Exception ex) when (ex is ModelProviderException or QuotaExceededException)
    {
        result = new CaseResult(evalCase, run, "", [], null, null, ex.Message);
    }
    results.Add(result);
    Console.WriteLine($"  {(result.Passed ? "OK  " : "ÉCHEC")} {evalCase.Id,-8} {evalCase.Category,-15} " +
                      (result.ContextFound == false ? "[contexte manquant] " : "") +
                      (result.Passed ? "" : string.Join(" ; ", result.FailureReasons)));
    if (opt.DelayMs > 0) await Task.Delay(opt.DelayMs);
}

var summary = EvalSummary.From(agent, provider, model, judge?.Label, retrievalMode.ToString().ToLowerInvariant(), results);
var reportPath = ReportWriter.Write(Path.Combine(root, opt.ReportDir), summary);

Console.WriteLine();
Console.WriteLine($"Score : {summary.Score:P0} ({summary.Passed}/{summary.Total})  seuil : {agent.Evaluation.MinScore:P0}  →  " +
                  (summary.MeetsThreshold ? "VALIDÉ" : "REFUSÉ"));
Console.WriteLine($"Recherche : sections attendues retrouvées {summary.ContextRecall.Found}/{summary.ContextRecall.Labeled}");
Console.WriteLine($"Coût : agent {summary.AgentCostUsd:0.######} $, juge {summary.JudgeCostUsd:0.######} $ · latence médiane {summary.MedianLatencyMs:0} ms");
Console.WriteLine($"Rapport : {Path.GetRelativePath(root, reportPath)}");

// Code de sortie non nul sous le seuil : c'est ce qui permet à la CI de bloquer le passage en production.
return summary.MeetsThreshold || opt.NoGate ? 0 : 1;

static async Task<T> WithRetry<T>(Func<Task<T>> action)
{
    var delays = new[] { 5, 15, 30 };
    for (var attempt = 0; ; attempt++)
    {
        try { return await action(); }
        catch (ModelProviderException ex) when (attempt < delays.Length && ex.HttpStatus is 429 or >= 500)
        {
            Console.WriteLine($"    (HTTP {ex.HttpStatus}, nouvelle tentative dans {delays[attempt]} s)");
            await Task.Delay(TimeSpan.FromSeconds(delays[attempt]));
        }
    }
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "agents")) && Directory.Exists(Path.Combine(d.FullName, "evals")))
            return d.FullName;
    throw new DirectoryNotFoundException("Racine du dépôt introuvable (dossiers agents/ et evals/).");
}

internal sealed record EvalOptions(string Agent, string? Provider, string? Model, string? Judge, int Repeat, int DelayMs, bool NoGate, string ReportDir,
    RetrievalMode? Retrieval, bool RetrievalOnly)
{
    public static EvalOptions Parse(string[] args)
    {
        string? Get(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return new EvalOptions(
            Get("--agent") ?? throw new ArgumentException("--agent est obligatoire"),
            Get("--provider"), Get("--model"), Get("--judge"),
            int.TryParse(Get("--repeat"), out var r) && r > 0 ? r : 1,
            int.TryParse(Get("--delay-ms"), out var d) && d >= 0 ? d : 1000,
            args.Contains("--no-gate"),
            Get("--report-dir") ?? Path.Combine("evals", "reports"),
            Get("--retrieval") is { } m ? Enum.Parse<RetrievalMode>(m, ignoreCase: true) : null,
            args.Contains("--retrieval-only"));
    }
}
