using System.Text.RegularExpressions;
using Eap.Core.Guardrails;

namespace Eap.Evals;

/// <param name="Arbitrable">Échec qu'un modèle juge peut lever (fragment suspect) ; les autres échecs sont définitifs.</param>
public sealed record CheckResult(string Name, bool Passed, string? Detail = null, bool Arbitrable = false);

/// <summary>
/// Contrôles déterministes : rapides, gratuits, reproductibles. Ils couvrent les exigences vérifiables
/// mécaniquement (faits attendus, absence d'invention, escalade, sources, longueur).
/// </summary>
public static partial class ResponseChecks
{
    public const string EscalationPhoneDigits = "0200000000";
    public const int MaxSentences = 6; // le prompt demande 5 phrases ; tolérance d'une phrase
    public const string SuspectCheck = "ne contient pas (à arbitrer)";

    public static IReadOnlyList<CheckResult> Run(EvalCase evalCase, string answer, KnownSources known)
    {
        var results = new List<CheckResult>();
        var norm = Normalize(answer);
        var e = evalCase.Expect;

        foreach (var expected in e.All)
            results.Add(new("contient", norm.Contains(Normalize(expected)), expected));

        foreach (var group in e.Any)
            results.Add(new("contient l'un de", group.Any(g => norm.Contains(Normalize(g))), string.Join(" | ", group)));

        foreach (var forbidden in e.None)
            results.Add(new("ne contient pas", !norm.Contains(Normalize(forbidden)), forbidden));

        foreach (var suspect in e.Suspect)
            results.Add(new(SuspectCheck, !norm.Contains(Normalize(suspect)), suspect, Arbitrable: true));

        if (e.Escalate)
            results.Add(new("escalade vers un conseiller", DigitsOnly(answer).Contains(EscalationPhoneDigits)));

        results.Add(CheckSources(answer, e.Source, known));

        var sentences = CountSentences(answer);
        results.Add(new("longueur", sentences <= MaxSentences, $"{sentences} phrases (max {MaxSentences})"));

        return results;
    }

    /// <summary>
    /// La ligne « Source : … » doit exister (si exigée) et ne citer que des documents réels.
    /// Une source inventée est un défaut grave : elle donne une fausse garantie de traçabilité.
    /// </summary>
    public static CheckResult CheckSources(string answer, SourceRequirement requirement, KnownSources known)
    {
        var line = ExtractSourceLine(answer);
        if (line is null)
            return new("source citée", requirement == SourceRequirement.Optional,
                requirement == SourceRequirement.Optional ? "aucune (facultative)" : "ligne « Source : » absente");

        var leftover = known.UnknownPart(line);
        return leftover is null
            ? new("source citée", true, line)
            : new("source citée", false, $"source inventée : « {leftover} » (ligne : {line})");
    }

    public static string? ExtractSourceLine(string answer) => SourceLine.Extract(answer);

    public static int CountSentences(string answer)
    {
        var body = SourceLine.Remove(answer);
        body = Regex.Replace(body, @"\bréf\.", "réf", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"(\d)[.,](\d)", "$1$2");
        return Regex.Split(body, @"(?<=[.!?])\s+|\n+")
            .Count(s => s.Count(char.IsLetter) >= 3);
    }

    public static string Normalize(string text) => TextNormalizer.Normalize(text);

    private static string DigitsOnly(string text) => new(text.Where(char.IsAsciiDigit).ToArray());
}
