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

/// <summary>
/// ExÃ©cute un tour de conversation : garde-fous FinOps â†’ rÃ©cupÃ©ration du contexte â†’
/// appel du modÃ¨le â†’ enregistrement de la consommation et traÃ§abilitÃ©.
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
            throw new InvalidOperationException($"L'agent {agent.Id} est retirÃ© du service.");

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        EnforceBudget(agent, userId, today);

        using var activity = Activity.StartActivity($"agent {agent.Id}");
        activity?.SetTag("eap.agent.id", agent.Id);
        activity?.SetTag("eap.agent.version", agent.Version);
        activity?.SetTag("eap.agent.status", agent.Status.ToString());
        activity?.SetTag("eap.user.id", userId);

        var chunks = knowledgeFor(agent).Search(question);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, agent.SystemPrompt),
            new(ChatRole.System, BuildContext(chunks)),
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
        var response = await clients.Create(provider, model).GetResponseAsync(messages, options, ct);
        var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var inputTokens = response.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Usage?.OutputTokenCount ?? 0;
        var price = clients.PriceOf(provider, model);
        var cost = price?.Cost(inputTokens, outputTokens) ?? 0m;

        var traceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var usage = new UsageRecord(
            _time.GetUtcNow(), traceId, agent.Id, agent.Version, userId,
            provider, model, inputTokens, outputTokens,
            cost, CostEstimated: price is null, latency);
        ledger.Record(usage);

        activity?.SetTag("eap.cost.usd", (double)cost);
        activity?.SetTag("eap.sources.count", chunks.Count);

        var sources = chunks.Select(c => $"{c.Source} â€º {c.Title}").Distinct().ToList();
        return new AgentReply(response.Text, traceId, sources, usage);
    }

    private void EnforceBudget(AgentManifest agent, string userId, DateOnly today)
    {
        if (ledger.CostToday(agent.Id, today) >= agent.Budget.DailyUsd)
            throw new QuotaExceededException(
                $"Budget quotidien de l'agent {agent.Id} atteint ({agent.Budget.DailyUsd} USD).");

        if (ledger.RequestsToday(agent.Id, userId, today) >= agent.Budget.MaxRequestsPerUserPerDay)
            throw new QuotaExceededException(
                $"Quota atteint : {agent.Budget.MaxRequestsPerUserPerDay} requÃªtes par jour et par utilisateur.");
    }

    private static string BuildContext(IReadOnlyList<KnowledgeChunk> chunks)
    {
        if (chunks.Count == 0)
            return "CONTEXTE : aucun document pertinent trouvÃ©. Si la question nÃ©cessite une information factuelle, dis-le et propose l'escalade vers un conseiller.";

        var sb = new StringBuilder("CONTEXTE (n'utilise que ces informations pour les faits) :\n\n");
        foreach (var c in chunks)
            sb.Append("[source: ").Append(c.Source).Append(" â€º ").Append(c.Title).Append("]\n")
              .Append(c.Content).Append("\n\n");
        return sb.ToString();
    }
}
