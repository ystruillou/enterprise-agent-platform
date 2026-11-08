using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eap.Evals;

public sealed record CheckResult(string Name, bool Passed, string? Detail = null);

/// <summary>
/// Contrôles déterministes : rapides, gratuits, reproductibles. Ils couvrent les exigences vérifiables
/// mécaniquement (faits attendus, absence d'invention, escalade, sources, longueur).
/// </summary>
public static partial class ResponseChecks
{
    public const string EscalationPhoneDigits = "0200000000";
    public const int MaxSentences = 6; // le prompt demande 5 phrases ; tolérance d'une phrase

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

    public static string? ExtractSourceLine(string answer)
    {
        var m = SourceLineRegex().Match(answer);
        return m.Success ? m.Groups["v"].Value.Trim().TrimEnd('.', '*').Trim() : null;
    }

    public static int CountSentences(string answer)
    {
        var body = SourceLineRegex().Replace(answer, "");
        body = Regex.Replace(body, @"\bréf\.", "réf", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"(\d)[.,](\d)", "$1$2");
        return Regex.Split(body, @"(?<=[.!?])\s+|\n+")
            .Count(s => s.Count(char.IsLetter) >= 3);
    }

    public static string Normalize(string text)
    {
        var s = text.Replace(' ', ' ').Replace(' ', ' ').Replace('’', '\'').ToLowerInvariant();
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"(\d)\.(\d)", "$1,$2"); // 39.90 -> 39,90
        s = s.Replace("**", "");                     // gras Markdown
        return Regex.Replace(s, @"\s+", " ");
    }

    private static string DigitsOnly(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex(@"^\s*\**\s*sources?\s*\**\s*:\s*\**(?<v>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SourceLineRegex();

}

/// <summary>
/// Documents et sections réellement présents dans la base de connaissances de l'agent.
/// Méthode : on retire de la citation tout ce qui est connu (fichiers, titres de section, mots de liaison) ;
/// s'il reste du texte, c'est une source inventée. On ne découpe pas la ligne, car les titres eux-mêmes
/// contiennent « et » ou des virgules (« Retours et remboursement »).
/// </summary>
public sealed partial class KnownSources
{
    private static readonly HashSet<string> Connectors =
        ["section", "sections", "rubrique", "partie", "document", "documents", "fichier", "fiche", "fiches", "produit", "produits",
         "et", "de", "du", "des", "la", "le", "les", "l", "d", "voir"];

    private readonly List<string> _terms; // du plus long au plus court, pour retirer « Garantie » après les titres qui le contiennent

    public KnownSources(IEnumerable<string> fileNames, IEnumerable<string> sectionTitles)
    {
        var files = fileNames.Select(ResponseChecks.Normalize).Distinct().ToList();
        var titles = sectionTitles.Select(ResponseChecks.Normalize).Distinct().ToList();
        // Les titres sont souvent cités sans leur référence : « Aspirateur sans fil Kerlenn Cyclone » sans « (ref. AS-CY2) ».
        var shortTitles = titles.Select(t => RefSuffixRegex().Replace(t, "").Trim()).Where(t => t.Length >= 4);
        _terms = files.Concat(titles).Concat(shortTitles).Distinct().OrderByDescending(t => t.Length).ToList();
        _stems = files.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        // Vocabulaire des titres : une citation abrégée (« catalogue.md › Aspirateur ») reste valide.
        _vocabulary = titles.Concat(_stems).SelectMany(t => WordRegex().Matches(t).Select(m => m.Value)).ToHashSet();
    }

    private readonly List<string> _stems;
    private readonly HashSet<string> _vocabulary;

    public bool IsKnown(string citation) => UnknownPart(citation) is null;

    /// <summary>Partie non reconnue de la citation, ou null si tout est connu.</summary>
    public string? UnknownPart(string citation)
    {
        var c = ResponseChecks.Normalize(citation);
        if (c.StartsWith("aucun")) return null; // « aucun document pertinent » : honnête, donc valide
        // Nom de fichier sans extension accepté seulement s'il est cité seul :
        // « Catalogue général Maison Kerlenn » ne doit pas passer parce qu'il contient « catalogue ».
        if (_stems.Contains(c.Trim())) return null;

        foreach (var term in _terms)
            c = c.Replace(term, " ");
        var leftover = WordRegex().Matches(c).Select(m => m.Value)
            .Where(w => !Connectors.Contains(w) && !_vocabulary.Contains(w)).ToList();
        return leftover.Count == 0 ? null : string.Join(" ", leftover);
    }

    [GeneratedRegex(@"\s*\(ref[.,]? [^)]*\)")]
    private static partial Regex RefSuffixRegex();

    [GeneratedRegex(@"[a-z]{2,}")]
    private static partial Regex WordRegex();
}
