using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eap.Evals;

/// <summary>Un cas de test : une question et ce que la réponse doit (ou ne doit pas) contenir.</summary>
public sealed record EvalCase
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Question { get; init; } = "";
    public Expectations Expect { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static IReadOnlyList<EvalCase> LoadJsonl(string path)
    {
        var cases = new List<EvalCase>();
        var lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = JsonSerializer.Deserialize<EvalCase>(line, Json)
                ?? throw new InvalidDataException($"{path}:{lineNo} : ligne vide ou invalide");
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Question))
                throw new InvalidDataException($"{path}:{lineNo} : id et question sont obligatoires");
            cases.Add(c);
        }
        if (cases.Select(c => c.Id).Distinct().Count() != cases.Count)
            throw new InvalidDataException($"{path} : identifiants de cas en double");
        return cases;
    }
}

public sealed record Expectations
{
    /// <summary>Tous ces fragments doivent apparaître.</summary>
    public List<string> All { get; init; } = [];
    /// <summary>Pour chaque groupe, au moins un des fragments doit apparaître (synonymes : "30 jours" / "trente jours").</summary>
    public List<List<string>> Any { get; init; } = [];
    /// <summary>Aucun de ces fragments ne doit apparaître (fait inventé, fuite du prompt, donnée personnelle…).</summary>
    public List<string> None { get; init; } = [];
    /// <summary>
    /// Fragments suspects : interdits comme <see cref="None"/>, mais leur présence peut être légitime
    /// (« les 5 ans ne s'appliquent pas à cette lampe »). Un mot-clé ne distingue pas une affirmation d'une négation :
    /// avec un modèle juge, c'est lui qui tranche ; sans juge, le cas échoue par prudence.
    /// À réserver aux faits métier : une fuite (prompt, numéro de carte) reste dans <see cref="None"/>, jamais arbitrable.
    /// </summary>
    public List<string> Suspect { get; init; } = [];
    /// <summary>Critère propre au cas, transmis au juge (ce qui rend la réponse juste ou fausse).</summary>
    public string? Rubric { get; init; }
    /// <summary>La réponse doit orienter vers un conseiller (numéro du service client).</summary>
    public bool Escalate { get; init; }
    /// <summary>Citation de source : obligatoire pour une réponse factuelle, facultative sinon (mais jamais inventée).</summary>
    public SourceRequirement Source { get; init; } = SourceRequirement.Required;
    /// <summary>
    /// Sections que la recherche documentaire doit retrouver (début du titre ; « A|B » = l'une ou l'autre).
    /// Mesure la recherche indépendamment du modèle : un modèle ne peut pas citer un fait qu'il n'a pas reçu.
    /// </summary>
    public List<string> Context { get; init; } = [];

    /// <summary>Toutes les sections attendues figurent-elles parmi les titres retrouvés ? null si le cas n'en attend aucune.</summary>
    public bool? ContextFound(IEnumerable<string> retrievedTitles)
    {
        if (Context.Count == 0) return null;
        var titles = retrievedTitles.Select(ResponseChecks.Normalize).ToList();
        return Context.All(label => label.Split('|').Select(ResponseChecks.Normalize)
            .Any(alt => titles.Any(t => t.StartsWith(alt.Trim()))));
    }
}

public enum SourceRequirement { Required, Optional }
