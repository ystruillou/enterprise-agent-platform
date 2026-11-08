using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eap.Core.Agents;

/// <summary>
/// Registre des agents : charge et valide toutes les fiches <c>agents/*/agent.yaml</c>.
/// </summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentManifest> _agents;

    private AgentRegistry(Dictionary<string, AgentManifest> agents) => _agents = agents;

    public IReadOnlyCollection<AgentManifest> All => _agents.Values;

    public AgentManifest? Find(string id) => _agents.GetValueOrDefault(id);

    public static AgentRegistry LoadFrom(string agentsRoot)
    {
        if (!Directory.Exists(agentsRoot))
            throw new DirectoryNotFoundException($"Répertoire des agents introuvable : {agentsRoot}");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var agents = new Dictionary<string, AgentManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(agentsRoot, "agent.yaml", SearchOption.AllDirectories))
        {
            var manifest = deserializer.Deserialize<AgentManifest>(File.ReadAllText(file));
            manifest.BaseDirectory = Path.GetDirectoryName(file)!;

            var errors = Validate(manifest).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"Fiche agent invalide ({file}) :{Environment.NewLine}- " + string.Join(Environment.NewLine + "- ", errors));

            manifest.SystemPrompt = File.ReadAllText(Path.Combine(manifest.BaseDirectory, manifest.PromptFile));

            if (!agents.TryAdd(manifest.Id, manifest))
                throw new InvalidOperationException($"Identifiant d'agent en double : {manifest.Id}");
        }
        return new AgentRegistry(agents);
    }

    /// <summary>Règles de gouvernance minimales vérifiées au chargement.</summary>
    public static IEnumerable<string> Validate(AgentManifest m)
    {
        if (string.IsNullOrWhiteSpace(m.Id)) yield return "id manquant";
        if (string.IsNullOrWhiteSpace(m.Name)) yield return "name manquant";
        if (string.IsNullOrWhiteSpace(m.Purpose)) yield return "purpose (finalité) manquante";
        if (string.IsNullOrWhiteSpace(m.Owners.Business)) yield return "owners.business (responsable métier) manquant";
        if (string.IsNullOrWhiteSpace(m.Owners.Tech)) yield return "owners.tech (responsable technique) manquant";
        if (m.Budget.DailyUsd <= 0) yield return "budget.daily_usd doit être > 0";
        if (m.Budget.MaxRequestsPerUserPerDay <= 0) yield return "budget.max_requests_per_user_per_day doit être > 0";
        if (m.Status == AgentStatus.Production && string.IsNullOrWhiteSpace(m.Evaluation.Dataset))
            yield return "un agent en production doit avoir un jeu d'évaluation";
        if (m.Risk.AiAct == AiActRiskLevel.High && m.Status == AgentStatus.Production && string.IsNullOrWhiteSpace(m.Risk.Notes))
            yield return "un agent à haut risque (AI Act) doit documenter ses mesures dans risk.notes";
        if (m.Retrieval.Top is < 1 or > 20) yield return "retrieval.top doit être compris entre 1 et 20";
        if (m.Retrieval.MinSimilarity is < 0 or >= 1) yield return "retrieval.min_similarity doit être compris entre 0 et 1";
        if (m.Evaluation.MaxSentences is < 1) yield return "evaluation.max_sentences doit être >= 1";
        if (m.Retrieval.Mode is Knowledge.RetrievalMode.Semantic or Knowledge.RetrievalMode.Hybrid
            && (string.IsNullOrWhiteSpace(m.Retrieval.EmbeddingProvider) || string.IsNullOrWhiteSpace(m.Retrieval.EmbeddingModel)))
            yield return "la recherche sémantique exige retrieval.embedding_provider et retrieval.embedding_model";
        if (!string.IsNullOrEmpty(m.BaseDirectory) && !File.Exists(Path.Combine(m.BaseDirectory, m.PromptFile)))
            yield return $"prompt_file introuvable : {m.PromptFile}";
    }
}
