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

/// <summary>Mention « Source : … » dans une réponse : position de la mention et texte qui suit les deux-points (jusqu'à la fin de la ligne).</summary>
public sealed record Citation(int Start, int Length, string Text)
{
    /// <summary>Référence citée, sans le commentaire qui la suit.</summary>
    public string Reference => SourceLine.SplitReference(Text).Reference;
    /// <summary>Commentaire qui suit la référence sur la même ligne (format conformité : « Source : règle. Explication… »).</summary>
    public string Comment => SourceLine.SplitReference(Text).Comment;
}

/// <summary>Mentions « Source : … » d'une réponse d'agent.</summary>
public static partial class SourceLine
{
    /// <summary>Première mention (texte complet de la ligne), pour l'affichage.</summary>
    public static string? Extract(string answer)
    {
        var m = Regex().Match(answer);
        return m.Success ? m.Groups["v"].Value.Trim().TrimEnd('.', '*').Trim() : null;
    }

    /// <summary>Toutes les mentions : un relecteur de conformité cite une règle par problème, pas une seule source finale.</summary>
    public static IReadOnlyList<Citation> All(string answer) =>
        Regex().Matches(answer).Select(m => new Citation(m.Index, m.Length, m.Groups["v"].Value)).ToList();

    public static string Remove(string answer) => Regex().Replace(answer, "");

    /// <summary>
    /// Sépare la référence du commentaire : la référence s'arrête à la première fin de phrase hors parenthèses.
    /// « regles.md › Crédit (Code de la consommation, art. L312). Le texte doit… » → référence « regles.md › Crédit (…) ».
    /// Les parenthèses sont ignorées pour la coupure : « art. L312 » ou « réf. AS-CY2 » ne terminent pas la référence.
    /// </summary>
    public static (string Reference, string Comment) SplitReference(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (c == '.' && depth == 0)
            {
                var j = i + 1;
                while (j < text.Length && text[j] == '*') j++;           // « prix**. Toute… » : fin de gras Markdown
                if (j == text.Length || char.IsWhiteSpace(text[j]))       // « catalogue.md » n'est pas une fin de phrase
                    return (text[..i].Trim().TrimEnd('*').Trim(), text[j..].Trim());
            }
        }
        return (text.Trim().TrimEnd('*').Trim(), "");
    }

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

    /// <param name="contents">
    /// Texte des sections : une parenthèse dont tous les mots en proviennent est une précision tirée du document
    /// (« (Code de la consommation, art. L112-1-1) »), pas une source inventée. Le contenu ne valide QUE les parenthèses :
    /// hors parenthèses, une citation faite de mots du contenu (« Consignes service client ») reste une invention.
    /// </param>
    public KnownSources(IEnumerable<string> fileNames, IEnumerable<string> sectionTitles, IEnumerable<string>? contents = null)
    {
        _contentVocabulary = (contents ?? []).SelectMany(t => WordRegex().Matches(TextNormalizer.Normalize(t)).Select(m => m.Value)).ToHashSet();
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
    private readonly HashSet<string> _contentVocabulary;

    public bool IsKnown(string citation) => UnknownPart(citation) is null;

    /// <summary>Partie non reconnue de la référence citée (le commentaire qui la suit est ignoré), ou null si tout est connu.</summary>
    public string? UnknownPart(string citation)
    {
        var c = TextNormalizer.Normalize(SourceLine.SplitReference(citation).Reference);
        if (c.StartsWith("aucun")) return null; // « aucun document pertinent » : honnête, donc valide
        // Nom de fichier sans extension accepté seulement s'il est cité seul :
        // « Catalogue général Maison Kerlenn » ne doit pas passer parce qu'il contient « catalogue ».
        if (_stems.Contains(c.Trim())) return null;

        foreach (var term in _terms)
            c = c.Replace(term, " ");
        c = ParenthesisRegex().Replace(c, m => WordRegex().Matches(m.Value).All(w => IsKnownWord(w.Value) || _contentVocabulary.Contains(w.Value)) ? " " : m.Value);
        var leftover = WordRegex().Matches(c).Select(m => m.Value)
            .Where(w => !Connectors.Contains(w) && !_vocabulary.Contains(w)).ToList();
        return leftover.Count == 0 ? null : string.Join(" ", leftover);
    }

    private bool IsKnownWord(string w) => Connectors.Contains(w) || _vocabulary.Contains(w);

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex ParenthesisRegex();

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
        var invalid = SourceLine.All(answer).Where(c => known.UnknownPart(c.Text) is not null).ToList();
        if (invalid.Count == 0) return (answer, null);

        // De la fin vers le début, pour que les positions restent valides. Seule la référence est remplacée :
        // le commentaire qui la suit (l'argument d'un relecteur de conformité) est conservé.
        var sb = new System.Text.StringBuilder(answer);
        foreach (var c in invalid.OrderByDescending(c => c.Start))
            sb.Remove(c.Start, c.Length).Insert(c.Start, c.Comment.Length == 0 ? Replacement : $"{Replacement} {c.Comment}");

        // Le texte inventé n'est pas recopié dans le détail : il peut provenir de la question (données utilisateur).
        return (sb.ToString(), new GuardrailAction(RuleName, invalid.Count == 1 ? "citation remplacée" : $"{invalid.Count} citations remplacées"));
    }
}
