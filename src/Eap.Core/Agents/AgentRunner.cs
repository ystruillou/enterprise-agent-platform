using System.Diagnostics;
using System.Text;
using Eap.Core.FinOps;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Microsoft.Extensions.AI;

namespace Eap.Core.Agents;

public sealed record AgentReply(
    string Answer,
    string TraceId,
    IReadOnlyList<string> Sources,
    UsageRecord Usage);

/// <summary>Force un fournisseur/modèle pour tous les agents (dev, CI, démo sans clé).</summary>
public sealed record ModelOverride(string Provider, string Model);

public sealed class QuotaExceededException(string message) : Exception(message);

/// <summary>Échec côté fournisseur de modèle (clé invalide, modèle retiré, indisponibilité…).</summary>
public sealed class ModelProviderException(string provider, string model, Exception inner)
    : Exception($"Le fournisseur {provider} a échoué pour le modèle {model} : {inner.Message} {ResponseBody(inner)}", inner)
{
    public string Provider { get; } = provider;
    public string Model { get; } = model;

    /// <summary>Corps de la réponse HTTP d'erreur du fournisseur, pour le diagnostic (journaux uniquement).</summary>
    private static string ResponseBody(Exception ex) =>
        ex is System.ClientModel.ClientResultException cre && cre.GetRawResponse() is { } raw
            ? raw.Content.ToString() is { Length: > 0 } body ? body[..Math.Min(body.Length, 1000)] : ""
            : "";
}

/// <summary>
/// Exécute un tour de conversation : garde-fous FinOps → récupération du contexte →
/// appel du modèle → enregistrement de la consommation et traçabilité.
/// </summary>
public sealed class AgentRunner(
    ChatClientFactory clients,
    IUsageLedger ledger,
    Func<AgentManifest, KnowledgeBase> knowledgeFor,
    TimeProvider? time = null,
    ModelOverride? modelOverride = null)
{
    public static readonly ActivitySource Activity = new("Eap.Agents");
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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

        var chunks = knowledgeFor(agent).Search(question);
        // Un SEUL message système : la couche compatible OpenAI de Gemini ne conserve que le dernier
        // message système, ce qui faisait disparaître les règles de l'agent (constaté le 2026-11-08).
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, agent.SystemPrompt.TrimEnd() + "\n\n" + BuildContext(chunks)),
            new(ChatRole.User, question),
        };
        var options = new ChatOptions
        {
            ModelId = agent.Model.Name,
            Temperature = agent.Model.Temperature,
            MaxOutputTokens = agent.Model.MaxOutputTokens,
        };

        var provider = modelOverride?.Provider ?? agent.Model.Provider;
        var model = modelOverride?.Model ?? agent.Model.Name;
        options.ModelId = model;
        activity?.SetTag("eap.model.provider", provider);

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

        var sources = chunks.Select(c => $"{c.Source} › {c.Title}").Distinct().ToList();
        return new AgentReply(response.Text, traceId, sources, usage);
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
