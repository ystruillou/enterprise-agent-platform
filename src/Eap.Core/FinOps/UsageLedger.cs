using System.Collections.Concurrent;

namespace Eap.Core.FinOps;

/// <summary>Une ligne de consommation : un appel de modèle, rattaché à un agent et à un utilisateur.</summary>
public sealed record UsageRecord(
    DateTimeOffset Timestamp,
    string TraceId,
    string AgentId,
    string AgentVersion,
    string UserId,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    bool CostEstimated,
    double LatencyMs);

public sealed record UsageSummary(
    string AgentId,
    string Provider,
    string Model,
    int Requests,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

/// <summary>Registre de consommation. Implémentation en mémoire pour l'instant (PostgreSQL ou BigQuery ensuite).</summary>
public interface IUsageLedger
{
    void Record(UsageRecord record);
    decimal CostToday(string agentId, DateOnly day);
    int RequestsToday(string agentId, string userId, DateOnly day);
    IReadOnlyList<UsageSummary> Summarize(DateOnly day);
}

public sealed class InMemoryUsageLedger : IUsageLedger
{
    private readonly ConcurrentQueue<UsageRecord> _records = new();

    public void Record(UsageRecord record) => _records.Enqueue(record);

    public decimal CostToday(string agentId, DateOnly day) =>
        OfDay(day).Where(r => r.AgentId == agentId).Sum(r => r.CostUsd);

    public int RequestsToday(string agentId, string userId, DateOnly day) =>
        OfDay(day).Count(r => r.AgentId == agentId && r.UserId == userId);

    public IReadOnlyList<UsageSummary> Summarize(DateOnly day) =>
        OfDay(day)
            .GroupBy(r => (r.AgentId, r.Provider, r.Model))
            .Select(g => new UsageSummary(
                g.Key.AgentId, g.Key.Provider, g.Key.Model, g.Count(),
                g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.CostUsd)))
            .OrderByDescending(s => s.CostUsd)
            .ToList();

    private IEnumerable<UsageRecord> OfDay(DateOnly day) =>
        _records.Where(r => DateOnly.FromDateTime(r.Timestamp.UtcDateTime) == day);
}
