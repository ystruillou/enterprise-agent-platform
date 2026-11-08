using System.Diagnostics;
using System.Text;
using Eap.Core.FinOps;
using Eap.Core.Guardrails;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Microsoft.Extensions.AI;

namespace Eap.Core.Agents;

public sealed record AgentReply(
    string Answer,
    string TraceId,
    IReadOnlyList<string> Sources,
    UsageRecord Usage,
    bool Truncated = false,
    IReadOnlyList<GuardrailAction>? Guardrails = null,
    string? ModelAnswer = null);
// ModelAnswer : réponse du modèle AVANT garde-fous, renseignée seulement s'ils l'ont modifiée.
// Sert à l'évaluation (vérifier que le garde-fou ne corrige pas à tort) ; l'API ne l'expose pas.

/// <summary>Force un fournisseur/modèle pour tous les agents (dev, CI, démo sans clé).</summary>
public sealed record ModelOverride(string Provider, string Model);

public sealed class QuotaExceededException(string message) : Exception(message);

/// <summary>Échec côté fournisseur de modèle (clé invalide, modèle retiré, indisponibilité…).</summary>
public sealed class ModelProviderException : Exception
{
    public string Provider { get; }
    public string Model { get; }
    /// <summary>Code HTTP renvoyé par le fournisseur (0 si l'échec n'est pas une réponse HTTP).</summary>
    public int HttpStatus { get; }
    /// <summary>Code d'erreur du fournisseur (ex. PERMISSION_DENIED, rate_limit_error), filtré.</summary>
    public string? ProviderErrorCode { get; }

    public ModelProviderException(string provider, string model, Exception inner)
        : base(BuildMessage(provider, model, inner, out var status, out var code), inner)
    {
        Provider = provider;
        Model = model;
        HttpStatus = status;
        ProviderErrorCode = code;
    }

    // Le corps de la réponse n'est jamais repris tel quel : il peut contenir des extraits de la requête,
    // donc des données utilisateur. On n'en extrait qu'un code d'erreur court, aux caractères filtrés.
    private static string BuildMessage(string provider, string model, Exception inner, out int status, out string? code)
    {
        status = 0;
        code = null;
        if (inner is System.ClientModel.ClientResultException cre)
        {
            status = cre.Status;
            code = ExtractErrorCode(cre.GetRawResponse()?.Content?.ToString());
        }
        var detail = status > 0 ? $"HTTP {status}{(code is null ? "" : $" ({code})")}" : inner.GetType().Name;
        return $"Le fournisseur {provider} a échoué pour le modèle {model} : {detail}";
    }

    public static string? ExtractErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            // La couche compatible OpenAI de Gemini renvoie parfois un tableau : [{"error": {...}}]
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                root = root.GetArrayLength() > 0 ? root[0] : default;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("error", out var error) || error.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            foreach (var name in new[] { "status", "type", "code" }) // Google : status ; OpenAI/Anthropic : type ou code
                if (error.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && v.GetString() is { Length: > 0 and <= 64 } s && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                    return s;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }
}

/// <summary>
/// Exécute un tour de conversation : garde-fous FinOps → récupération du contexte →
/// appel du modèle → enregistrement de la consommation et traçabilité.
/// </summary>
public sealed class AgentRunner(
    ChatClientFactory clients,
    IUsageLedger ledger,
    Func<AgentManifest, IKnowledgeRetriever> knowledgeFor,
    TimeProvider? time = null,
    ModelOverride? modelOverride = null)
{
    public static readonly ActivitySource Activity = new("Eap.Agents");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IKnowledgeRetriever, KnownSources> _knownSources = new();

    public async Task<AgentReply> AskAsync(AgentManifest agent, string userId, string question, CancellationToken ct = default)
    {
        if (agent.Status == AgentStatus.Retired)
            throw new InvalidOperationException($"L'agent {agent.Id} est retiré du service.");

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        EnforceBudget(agent, userId, today);

        using var activity = Activity.StartActivity($"agent {agent.Id}");
        activity?.SetTag("eap.agent.id", agent.Id);
        activity?.SetTag("eap.agent.version", agent.Version);
        activity?.SetTag("eap.agent.status", agent.Status.ToString());
        activity?.SetTag("eap.user.id", userId);

        var retriever = knowledgeFor(agent);
        var retrieval = await retriever.SearchAsync(question, ct);
        var chunks = retrieval.Chunks;
        activity?.SetTag("eap.retrieval.mode", retrieval.Mode.ToString());
        activity?.SetTag("eap.retrieval.degraded", retrieval.Degraded);
        // Un SEUL message système : la couche compatible OpenAI de Gemini ne conserve que le dernier
        // message système, ce qui faisait disparaître les règles de l'agent (constaté le 2026-11-08).
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, agent.SystemPrompt.TrimEnd() + "\n\n" + BuildContext(chunks)),
            new(ChatRole.User, question),
        };
        var provider = modelOverride?.Provider ?? agent.Model.Provider;
        var model = modelOverride?.Model ?? agent.Model.Name;
        activity?.SetTag("eap.model.provider", provider);

        var options = new ChatOptions
        {
            ModelId = model,
            // Les fournisseurs qui refusent "temperature" (Claude récents) ne la reçoivent pas.
            Temperature = clients.SupportsTemperature(provider) ? agent.Model.Temperature : null,
            MaxOutputTokens = agent.Model.MaxOutputTokens,
        };

        var started = Stopwatch.GetTimestamp();
        ChatResponse response;
        try
        {
            response = await clients.Create(provider, model).GetResponseAsync(messages, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw new ModelProviderException(provider, model, ex);
        }
        var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Un alias (ex. "gemini-flash-latest") peut être servi par une autre version : on trace la version réelle.
        var servedModel = string.IsNullOrWhiteSpace(response.ModelId) ? model : response.ModelId;
        activity?.SetTag("eap.model.served", servedModel);

        var inputTokens = response.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Usage?.OutputTokenCount ?? 0;
        var price = clients.PriceOf(provider, model);
        var cost = price?.Cost(inputTokens, outputTokens) ?? 0m;

        var traceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var usage = new UsageRecord(
            _time.GetUtcNow(), traceId, agent.Id, agent.Version, userId,
            provider, model, servedModel, inputTokens, outputTokens,
            cost, CostEstimated: price is null, latency);
        ledger.Record(usage);

        activity?.SetTag("eap.cost.usd", (double)cost);
        activity?.SetTag("eap.sources.count", chunks.Count);

        // Réponse coupée par le plafond de tokens : sur les modèles qui raisonnent, le raisonnement interne
        // consomme le même budget que la réponse visible (constaté avec Claude Haiku 5.5 le 2026-11-08).
        var truncated = response.FinishReason == ChatFinishReason.Length;
        activity?.SetTag("eap.response.truncated", truncated);

        // Garde-fous de sortie : appliqués après facturation (l'appel a bien été consommé), avant de répondre.
        var answer = response.Text;
        var actions = new List<GuardrailAction>();
        if (agent.Guardrails.VerifySources && retriever.Corpus.Count > 0)
        {
            var (guarded, action) = SourceGuard.Apply(answer, _knownSources.GetOrAdd(retriever, r =>
                new KnownSources(r.Corpus.Select(c => c.Source), r.Corpus.Select(c => c.Title), r.Corpus.Select(c => c.Content))));
            answer = guarded;
            if (action is not null) actions.Add(action);
        }
        activity?.SetTag("eap.guardrail.actions", actions.Count);
        foreach (var a in actions)
            activity?.AddEvent(new ActivityEvent("eap.guardrail", tags: new ActivityTagsCollection { ["rule"] = a.Rule }));

        var sources = chunks.Select(c => $"{c.Source} › {c.Title}").Distinct().ToList();
        return new AgentReply(answer, traceId, sources, usage, truncated, actions,
            ModelAnswer: actions.Count > 0 ? response.Text : null);
    }

    private void EnforceBudget(AgentManifest agent, string userId, DateOnly today)
    {
        if (ledger.CostToday(agent.Id, today) >= agent.Budget.DailyUsd)
            throw new QuotaExceededException(
                $"Budget quotidien de l'agent {agent.Id} atteint ({agent.Budget.DailyUsd} USD).");

        if (ledger.RequestsToday(agent.Id, userId, today) >= agent.Budget.MaxRequestsPerUserPerDay)
            throw new QuotaExceededException(
                $"Quota atteint : {agent.Budget.MaxRequestsPerUserPerDay} requêtes par jour et par utilisateur.");
    }

    private static string BuildContext(IReadOnlyList<KnowledgeChunk> chunks)
    {
        if (chunks.Count == 0)
            return "CONTEXTE : aucun document pertinent trouvé. Si la question nécessite une information factuelle, dis-le et propose l'escalade vers un conseiller.";

        var sb = new StringBuilder("CONTEXTE (n'utilise que ces informations pour les faits) :\n\n");
        foreach (var c in chunks)
            sb.Append("[source: ").Append(c.Source).Append(" › ").Append(c.Title).Append("]\n")
              .Append(c.Content).Append("\n\n");
        return sb.ToString();
    }
}
