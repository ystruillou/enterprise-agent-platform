namespace Eap.Core.Agents;

/// <summary>
/// "Fiche agent" : description déclarative d'un agent, versionnée dans Git.
/// C'est à la fois la configuration technique et l'entrée du registre de gouvernance.
/// </summary>
public sealed class AgentManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.1.0";
    public AgentStatus Status { get; set; } = AgentStatus.Idea;
    public string Purpose { get; set; } = "";
    public AgentOwners Owners { get; set; } = new();
    public AgentRisk Risk { get; set; } = new();
    public AgentModel Model { get; set; } = new();
    public string PromptFile { get; set; } = "system.md";
    public string? KnowledgeDir { get; set; }
    public AgentBudget Budget { get; set; } = new();
    public AgentEvaluation Evaluation { get; set; } = new();

    /// <summary>Contenu du prompt système, chargé depuis <see cref="PromptFile"/>.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Répertoire racine de l'agent (non sérialisé).</summary>
    public string BaseDirectory { get; set; } = "";
}

/// <summary>Cycle de vie d'un agent, de l'idée au retrait.</summary>
public enum AgentStatus { Idea, Design, Pilot, Production, Retired }

public sealed class AgentOwners
{
    /// <summary>Responsable métier (le "A" du RACI).</summary>
    public string Business { get; set; } = "";
    /// <summary>Responsable technique (le "R" du RACI).</summary>
    public string Tech { get; set; } = "";
}

public sealed class AgentRisk
{
    /// <summary>Classification AI Act : minimal, limited, high (unacceptable = interdit).</summary>
    public AiActRiskLevel AiAct { get; set; } = AiActRiskLevel.Minimal;
    public bool PersonalData { get; set; }
    public string Notes { get; set; } = "";
}

public enum AiActRiskLevel { Minimal, Limited, High }

public sealed class AgentModel
{
    /// <summary>Clé du fournisseur dans la configuration (gemini, anthropic, openai, qwen, fake).</summary>
    public string Provider { get; set; } = "fake";
    public string Name { get; set; } = "fake-model";
    public float Temperature { get; set; } = 0.2f;
    public int MaxOutputTokens { get; set; } = 800;
}

public sealed class AgentBudget
{
    /// <summary>Plafond de dépense quotidien de l'agent, en USD.</summary>
    public decimal DailyUsd { get; set; } = 1m;
    /// <summary>Nombre maximal de requêtes par utilisateur et par jour.</summary>
    public int MaxRequestsPerUserPerDay { get; set; } = 50;
}

public sealed class AgentEvaluation
{
    public string? Dataset { get; set; }
    /// <summary>Score minimal (0..1) exigé pour passer en production.</summary>
    public double MinScore { get; set; } = 0.8;
}
