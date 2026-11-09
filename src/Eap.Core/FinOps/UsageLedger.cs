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
    string ServedModel,
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

/// <summary>
/// Registre de consommation : sert aux plafonds (budget par agent, quota par utilisateur) et au tableau FinOps.
/// En mémoire en développement et en évaluation ; persistant en production (Firestore), pour que les plafonds
/// survivent aux redémarrages et soient partagés entre instances.
/// </summary>
public interface IUsageLedger
{
    Task RecordAsync(UsageRecord record, CancellationToken ct = default);
    Task<decimal> CostTodayAsync(string agentId, DateOnly day, CancellationToken ct = default);
    Task<int> RequestsTodayAsync(string agentId, string userId, DateOnly day, CancellationToken ct = default);
    Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly day, CancellationToken ct = default);
}

public sealed class InMemoryUsageLedger : IUsageLedger
{
    private readonly ConcurrentQueue<UsageRecord> _records = new();

    public Task RecordAsync(UsageRecord record, CancellationToken ct = default)
    {
        _records.Enqueue(record);
        return Task.CompletedTask;
    }

    public Task<decimal> CostTodayAsync(string agentId, DateOnly day, CancellationToken ct = default) =>
        Task.FromResult(OfDay(day).Where(r => r.AgentId == agentId).Sum(r => r.CostUsd));

    public Task<int> RequestsTodayAsync(string agentId, string userId, DateOnly day, CancellationToken ct = default) =>
        Task.FromResult(OfDay(day).Count(r => r.AgentId == agentId && r.UserId == userId));

    public Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly day, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UsageSummary>>(OfDay(day)
            .GroupBy(r => (r.AgentId, r.Provider, r.Model))
            .Select(g => new UsageSummary(
                g.Key.AgentId, g.Key.Provider, g.Key.Model, g.Count(),
                g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.CostUsd)))
            .OrderByDescending(s => s.CostUsd)
            .ToList());

    private IEnumerable<UsageRecord> OfDay(DateOnly day) =>
        _records.Where(r => DateOnly.FromDateTime(r.Timestamp.UtcDateTime) == day);
}
