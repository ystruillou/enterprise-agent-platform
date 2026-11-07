namespace Eap.Core.Models;

/// <summary>
/// Configuration d'un fournisseur de modèles. Gemini, Claude, OpenAI et Qwen exposent tous
/// une API compatible OpenAI : un seul client suffit pour démarrer, on changera pour les SDK
/// natifs quand on aura besoin de fonctions propres à un fournisseur.
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>URL de base compatible OpenAI. Vide pour le fournisseur "fake".</summary>
    public string? Endpoint { get; set; }

    /// <summary>Nom de la variable d'environnement qui contient la clé (jamais la clé elle-même).</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>Tarifs par modèle, en USD par million de tokens.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModelPrice
{
    public decimal InputPerMillion { get; init; }
    public decimal OutputPerMillion { get; init; }

    public decimal Cost(long inputTokens, long outputTokens) =>
        (inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / 1_000_000m;
}
