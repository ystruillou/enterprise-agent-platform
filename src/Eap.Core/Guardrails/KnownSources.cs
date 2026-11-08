using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eap.Core.Guardrails;

/// <summary>Normalisation pour comparer des textes : accents, casse, espaces insécables, apostrophes, gras Markdown, décimales.</summary>
public static partial class TextNormalizer
{
    public static string Normalize(string text)
    {
        var s = text.Replace(' ', ' ').Replace(' ', ' ').Replace('’', '\'').ToLowerInvariant();
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = DecimalRegex().Replace(s, "$1,$2"); // 39.90 -> 39,90
        s = s.Replace("**", "");                // gras Markdown
        return SpacesRegex().Replace(s, " ");
    }

    [GeneratedRegex(@"(\d)\.(\d)")]
    private static partial Regex DecimalRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}

/// <summary>Ligne « Source : … » d'une réponse d'agent.</summary>
public static partial class SourceLine
{
    public static string? Extract(string answer)
    {
        var m = Regex().Match(answer);
        return m.Success ? m.Groups["v"].Value.Trim().TrimEnd('.', '*').Trim() : null;
    }

    public static string Remove(string answer) => Regex().Replace(answer, "");

    /// <summary>Remplace la (première) mention « Source : … » (jusqu'à la fin de la ligne) par <paramref name="replacement"/>.</summary>
    public static string Replace(string answer, string replacement) => Regex().Replace(answer, replacement, 1);

    // « Source : » en début de ligne, ou juste après une fin de phrase sur la même ligne (Claude l'écrit parfois ainsi) :
    // sans ce second cas, une source inventée « en ligne » échapperait au garde-fou.
    // [ \t] et non \s en début de ligne : \s avalerait la ligne vide qui précède, et le remplacement collerait la source au texte.
    [GeneratedRegex(@"(?:^[ \t]*|(?<=[.!?»)][ \t]+))\**[ \t]*sources?\s*\**\s*:\s*\**(?<v>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Regex();
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
        var files = fileNames.Select(TextNormalizer.Normalize).Distinct().ToList();
        var titles = sectionTitles.Select(TextNormalizer.Normalize).Distinct().ToList();
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
        var c = TextNormalizer.Normalize(citation);
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

/// <summary>Intervention d'un garde-fou sur une réponse, tracée et remontée à l'appelant.</summary>
public sealed record GuardrailAction(string Rule, string Detail);

/// <summary>
/// Garde-fou de sortie : une réponse ne peut citer que des documents de la base de connaissances.
/// Le prompt le demande déjà, mais un modèle n'obéit pas toujours (Gemini invente « Consignes générales » quand il n'a
/// pas de source). Le code, lui, le garantit : une citation invérifiable est remplacée par une mention honnête.
/// </summary>
public static class SourceGuard
{
    public const string RuleName = "source_inventee";
    public const string Replacement = "Source : aucune source vérifiable dans la documentation.";

    public static (string Answer, GuardrailAction? Action) Apply(string answer, KnownSources known)
    {
        var line = SourceLine.Extract(answer);
        if (line is null) return (answer, null);

        var unknown = known.UnknownPart(line);
        if (unknown is null) return (answer, null);

        // Le texte inventé n'est pas recopié dans le détail : il peut provenir de la question (données utilisateur).
        return (SourceLine.Replace(answer, Replacement), new GuardrailAction(RuleName, "citation remplacée"));
    }
}
