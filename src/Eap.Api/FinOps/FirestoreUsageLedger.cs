using System.Security.Cryptography;
using System.Text;
using Eap.Core.FinOps;
using Google.Cloud.Firestore;

namespace Eap.Api.FinOps;

/// <summary>
/// Registre de consommation persistant (Firestore, sans serveur, région Paris). Les plafonds survivent aux redémarrages
/// et sont partagés entre instances. On stocke des agrégats, pas chaque appel : le détail par appel est dans les traces.
/// </summary>
/// <remarks>
/// Compteurs incrémentés atomiquement (<see cref="FieldValue.Increment(long)"/>). Le contrôle d'un plafond, lui, n'est pas
/// atomique avec l'incrément : deux requêtes simultanées peuvent dépasser le quota d'une unité. C'est un plafond de
/// protection des coûts, pas une facturation : l'écart est accepté plutôt que de sérialiser toutes les requêtes.
/// </remarks>
public sealed class FirestoreUsageLedger(FirestoreDb db) : IUsageLedger
{
    public const string DailyCollection = "usage_daily";
    public const string UserCollection = "usage_user_daily";
    /// <summary>
    /// Nano-dollars entiers. Les micro-dollars arrondissaient chaque appel (0,0000283 $ comptés 28 µ$ au lieu de 28,3) :
    /// 1 % d'écart sur de petits appels, trouvé par les tests sur l'émulateur Firestore. Avec des tarifs au centime par
    /// million de tokens, un token coûte un nombre entier de nano-dollars : la somme est exacte. Plafond : 9 milliards de $.
    /// </summary>
    private const decimal NanoUsd = 1_000_000_000m;
    private const string CostField = "costNanoUsd";
    /// <summary>Rétention (politique TTL Firestore sur <c>expireAt</c>) : le quota d'un utilisateur ne sert que le jour même.</summary>
    public static readonly TimeSpan UserCounterRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan DailyAggregateRetention = TimeSpan.FromDays(400);

    public async Task RecordAsync(UsageRecord r, CancellationToken ct = default)
    {
        var day = DateOnly.FromDateTime(r.Timestamp.UtcDateTime);
        var batch = db.StartBatch();
        batch.Set(db.Collection(DailyCollection).Document(DailyId(day, r.AgentId, r.Provider, r.Model)), new Dictionary<string, object>
        {
            ["day"] = Day(day), ["agentId"] = r.AgentId, ["provider"] = r.Provider, ["model"] = r.Model,
            ["requests"] = FieldValue.Increment(1),
            ["inputTokens"] = FieldValue.Increment(r.InputTokens),
            ["outputTokens"] = FieldValue.Increment(r.OutputTokens),
            // Entiers : pas d'arrondi flottant qui s'accumule sur des milliers d'appels.
            [CostField] = FieldValue.Increment((long)Math.Round(r.CostUsd * NanoUsd)),
            ["expireAt"] = Expiry(day, DailyAggregateRetention),
        }, SetOptions.MergeAll);
        batch.Set(db.Collection(UserCollection).Document(UserId(day, r.AgentId, r.UserId)), new Dictionary<string, object>
        {
            ["day"] = Day(day), ["agentId"] = r.AgentId,
            ["requests"] = FieldValue.Increment(1),
            ["expireAt"] = Expiry(day, UserCounterRetention),
        }, SetOptions.MergeAll);
        await batch.CommitAsync(ct);
    }

    public async Task<decimal> CostTodayAsync(string agentId, DateOnly day, CancellationToken ct = default)
    {
        var snapshot = await db.Collection(DailyCollection)
            .WhereEqualTo("day", Day(day)).WhereEqualTo("agentId", agentId).GetSnapshotAsync(ct);
        return snapshot.Documents.Sum(Cost);
    }

    public async Task<int> RequestsTodayAsync(string agentId, string userId, DateOnly day, CancellationToken ct = default)
    {
        var doc = await db.Collection(UserCollection).Document(UserId(day, agentId, userId)).GetSnapshotAsync(ct);
        return doc.Exists ? (int)doc.GetValue<long>("requests") : 0;
    }

    public async Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly day, CancellationToken ct = default)
    {
        var snapshot = await db.Collection(DailyCollection).WhereEqualTo("day", Day(day)).GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(d => new UsageSummary(
                d.GetValue<string>("agentId"), d.GetValue<string>("provider"), d.GetValue<string>("model"),
                (int)d.GetValue<long>("requests"), d.GetValue<long>("inputTokens"), d.GetValue<long>("outputTokens"),
                Cost(d)))
            .OrderByDescending(s => s.CostUsd)
            .ToList();
    }

    /// <summary>
    /// Coût d'un agrégat. Lit aussi l'ancien champ en micro-dollars (première version déployée) : sans lui, la consommation
    /// déjà enregistrée compterait pour zéro et ne consommerait plus le budget du jour. Un document sans aucun champ de coût
    /// lisible est une anomalie : exception, donc requête refusée (fermé par défaut) plutôt qu'un coût nul.
    /// </summary>
    private static decimal Cost(DocumentSnapshot d)
    {
        var hasNano = d.TryGetValue<long>(CostField, out var nano);
        var hasMicro = d.TryGetValue<long>(LegacyMicroCostField, out var micro);
        if (!hasNano && !hasMicro)
            throw new InvalidDataException($"Agrégat de consommation sans coût lisible : {d.Reference.Path}");
        return (nano + micro * 1_000L) / NanoUsd;
    }

    private const string LegacyMicroCostField = "costMicroUsd";

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd");

    private static Timestamp Expiry(DateOnly day, TimeSpan retention) =>
        Timestamp.FromDateTime(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + retention);

    private static string DailyId(DateOnly day, string agent, string provider, string model) =>
        string.Join('|', Day(day), Safe(agent), Safe(provider), Safe(model));

    /// <summary>Identifiant d'utilisateur haché : même le pseudonyme Google n'est pas stocké.</summary>
    private static string UserId(DateOnly day, string agent, string userId) =>
        string.Join('|', Day(day), Safe(agent), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..32]);

    // « / » est interdit dans un identifiant de document Firestore.
    private static string Safe(string s) => s.Replace('/', '_');
}
