using Eap.Api.FinOps;
using Eap.Core.FinOps;
using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace Eap.Tests;

/// <summary>
/// Contrat commun à tous les registres de consommation : la production (Firestore) se comporte comme le développement.
/// « memoire » est toujours testé ; « firestore » seulement si l'émulateur tourne (FIRESTORE_EMULATOR_HOST, lancé en CI).
/// </summary>
public class LedgerTests
{
    public static TheoryData<string> Ledgers()
    {
        var data = new TheoryData<string> { "memoire" };
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"))) data.Add("firestore");
        return data;
    }

    private static IUsageLedger Create(string kind) => kind switch
    {
        "memoire" => new InMemoryUsageLedger(),
        "firestore" => new FirestoreUsageLedger(new FirestoreDbBuilder
        {
            ProjectId = "eap-test",
            EmulatorDetection = EmulatorDetection.EmulatorOnly, // jamais la vraie base depuis les tests
        }.Build()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Un agent par test : les tests Firestore partagent la même base d'émulateur.
    private readonly string _agent = "agent-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly DateOnly Day = new(2026, 11, 9);
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private UsageRecord Call(string user, decimal cost, string model = "m1", DateTimeOffset? at = null) =>
        new(at ?? Noon, "trace", _agent, "1.0", user, "gemini", model, model, 100, 20, cost, false, 500);

    [Theory]
    [MemberData(nameof(Ledgers))]
    public async Task Costs_add_up_per_agent_and_day_without_rounding_drift(string kind)
    {
        var ledger = Create(kind);
        for (var i = 0; i < 10; i++) await ledger.RecordAsync(Call("alice", 0.0000283m));
        await ledger.RecordAsync(Call("bob", 0.5m, model: "m2"));
        await ledger.RecordAsync(Call("alice", 9m, at: Noon.AddDays(1))); // autre jour

        Assert.Equal(0.500283m, await ledger.CostTodayAsync(_agent, Day));
    }

    [Theory]
    [MemberData(nameof(Ledgers))]
    public async Task Requests_are_counted_per_user_for_the_quota(string kind)
    {
        var ledger = Create(kind);
        await ledger.RecordAsync(Call("alice", 0.01m));
        await ledger.RecordAsync(Call("alice", 0.01m));
        await ledger.RecordAsync(Call("bob", 0.01m));

        Assert.Equal(2, await ledger.RequestsTodayAsync(_agent, "alice", Day));
        Assert.Equal(1, await ledger.RequestsTodayAsync(_agent, "bob", Day));
        Assert.Equal(0, await ledger.RequestsTodayAsync(_agent, "carol", Day));
    }

    [Theory]
    [MemberData(nameof(Ledgers))]
    public async Task Summary_groups_by_agent_and_model(string kind)
    {
        var ledger = Create(kind);
        await ledger.RecordAsync(Call("alice", 0.02m, model: "m1"));
        await ledger.RecordAsync(Call("bob", 0.03m, model: "m1"));
        await ledger.RecordAsync(Call("alice", 0.5m, model: "m2"));

        var lines = (await ledger.SummarizeAsync(Day)).Where(l => l.AgentId == _agent).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("m2", lines[0].Model); // le plus coûteux d'abord
        var m1 = lines.Single(l => l.Model == "m1");
        Assert.Equal((2, 200L, 40L, 0.05m), (m1.Requests, m1.InputTokens, m1.OutputTokens, m1.CostUsd));
    }
}
