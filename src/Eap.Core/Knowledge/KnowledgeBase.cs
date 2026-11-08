using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Eap.Core.Knowledge;

public sealed record KnowledgeChunk(string Source, string Title, string Content);

/// <summary>
/// Base de connaissances minimale : découpe les fichiers Markdown par titre et
/// classe les extraits par recouvrement de mots-clés (type BM25 simplifié).
/// Volontairement simple pour la semaine 1 ; sera remplacée par une recherche vectorielle.
/// </summary>
public sealed partial class KnowledgeBase
{
    private static readonly HashSet<string> StopWords =
    [
        "le", "la", "les", "un", "une", "des", "de", "du", "et", "ou", "a", "au", "aux", "en", "est", "je", "j",
        "vous", "nous", "il", "elle", "on", "ce", "ces", "cette", "que", "qui", "quoi", "quel", "quelle", "pour",
        "par", "sur", "dans", "avec", "mon", "ma", "mes", "votre", "vos", "pas", "ne", "se", "sa", "son", "ses",
        "l", "d", "qu", "c", "s", "n", "m", "t", "y", "est-ce", "comment", "puis", "peux", "peut",
    ];

    private readonly List<(KnowledgeChunk Chunk, HashSet<string> Terms)> _index = [];

    public int Count => _index.Count;

    public IReadOnlyList<KnowledgeChunk> Chunks => _index.Select(e => e.Chunk).ToList();

    public static KnowledgeBase Empty { get; } = new();

    public static KnowledgeBase LoadFrom(string directory)
    {
        var kb = new KnowledgeBase();
        if (!Directory.Exists(directory)) return kb;

        foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Order())
        {
            var source = Path.GetFileName(file);
            foreach (var chunk in Split(source, File.ReadAllText(file)))
                kb._index.Add((chunk, Tokenize(chunk.Title + " " + chunk.Content).ToHashSet()));
        }
        return kb;
    }

    public IReadOnlyList<KnowledgeChunk> Search(string query, int top = 3)
    {
        var queryTerms = Tokenize(query).Distinct().ToList();
        if (queryTerms.Count == 0) return [];

        return _index
            .Select(e => (e.Chunk, Score: queryTerms.Count(e.Terms.Contains)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(top)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static IEnumerable<KnowledgeChunk> Split(string source, string markdown)
    {
        string title = source;
        var buffer = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                if (buffer.ToString().Trim() is { Length: > 0 } content)
                    yield return new KnowledgeChunk(source, title, content);
                buffer.Clear();
                title = line.TrimStart('#').Trim();
            }
            else
            {
                buffer.AppendLine(line.TrimEnd('\r'));
            }
        }
        if (buffer.ToString().Trim() is { Length: > 0 } last)
            yield return new KnowledgeChunk(source, title, last);
    }

    internal static IEnumerable<string> Tokenize(string text) =>
        WordRegex().Matches(RemoveDiacritics(text.ToLowerInvariant()))
            .Select(m => m.Value)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(Stem);

    /// <summary>Racinisation très grossière : supprime le pluriel.</summary>
    private static string Stem(string word) =>
        word.Length > 3 && (word.EndsWith('s') || word.EndsWith('x')) ? word[..^1] : word;

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex WordRegex();
}
