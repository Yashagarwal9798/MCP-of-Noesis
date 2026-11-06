using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoesisMcp.Docs;

public sealed record DocChunk(string Title, string Section, string Url, string Text);
public sealed record DocHit(DocChunk Chunk, double Score);

/// <summary>
/// In-memory BM25 index over a local copy of the public Noesis docs (data/docs.json, made by
/// scripts/fetch-docs.py). Matches in titles and section headings count three times.
/// </summary>
public sealed partial class DocsIndex
{
    const double K1 = 1.2, B = 0.75, TitleBoost = 3.0;

    readonly List<DocChunk> _chunks = new();
    readonly List<Dictionary<string, double>> _tf = new();
    readonly List<double> _len = new();
    readonly Dictionary<string, int> _df = new();
    double _avgLen;

    public string? SourcePath { get; private set; }
    public int Count => _chunks.Count;

    public static DocsIndex LoadDefault()
    {
        var index = new DocsIndex();
        var path = Environment.GetEnvironmentVariable("NOESIS_MCP_DOCS") ?? FindUpwards(Path.Combine("data", "docs.json"));
        if (path != null && File.Exists(path))
        {
            var chunks = JsonSerializer.Deserialize<List<DocChunk>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (chunks != null) index.Add(chunks);
            index.SourcePath = path;
        }
        return index;
    }

    static string? FindUpwards(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Add(IEnumerable<DocChunk> chunks)
    {
        foreach (var c in chunks)
        {
            var tf = new Dictionary<string, double>();
            double len = 0;
            foreach (var t in Tokenize(c.Text)) { tf[t] = tf.GetValueOrDefault(t) + 1; len++; }
            foreach (var t in Tokenize(c.Title + " " + c.Section)) { tf[t] = tf.GetValueOrDefault(t) + TitleBoost; len++; }
            foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
            _chunks.Add(c); _tf.Add(tf); _len.Add(len);
        }
        _avgLen = _len.Count == 0 ? 0 : _len.Average();
    }

    public List<DocHit> Search(string query, int max)
    {
        var terms = Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || _chunks.Count == 0) return new();
        int n = _chunks.Count;
        var scores = new List<DocHit>();
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            foreach (var term in terms)
            {
                if (!_tf[i].TryGetValue(term, out var f)) continue;
                double df = _df[term];
                double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                s += idf * f * (K1 + 1) / (f + K1 * (1 - B + B * _len[i] / _avgLen));
            }
            if (s <= 0) continue;
            var c = _chunks[i];
            // Exact class-name match ("DataTrigger" -> "DataTrigger Class") is what people want first.
            if (terms.Any(t => c.Title.StartsWith(t + " ", StringComparison.OrdinalIgnoreCase))) s *= 1.5;
            if (c.Url.Contains("Tutorial", StringComparison.Ordinal) || c.Url.Contains("Gui.Studio", StringComparison.Ordinal)) s *= 1.25;
            if (c.Url.Contains("Changelog", StringComparison.Ordinal)) s *= 0.4;
            scores.Add(new DocHit(c, s));
        }
        // One result per page (its best section); best first.
        return scores.OrderByDescending(h => h.Score)
                     .DistinctBy(h => h.Chunk.Url)
                     .Take(Math.Clamp(max, 1, 10)).ToList();
    }

    public static string Snippet(DocChunk chunk, string query, int maxChars = 500)
    {
        var text = WhiteSpace().Replace(chunk.Text, " ").Trim();
        if (text.Length <= maxChars) return text;
        var terms = Tokenize(query).ToList();
        // Start the snippet near the first matching term.
        int best = 0;
        foreach (var t in terms)
        {
            var at = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) { best = at; break; }
        }
        int start = Math.Max(0, best - 120);
        if (start > 0) { var sp = text.IndexOf(' ', start); start = sp > 0 && sp < best ? sp + 1 : start; }
        var snippet = text.Substring(start, Math.Min(maxChars, text.Length - start));
        return (start > 0 ? "…" : "") + snippet + (start + snippet.Length < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*|\d+")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelSplit();

    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    { "the", "a", "an", "of", "to", "in", "and", "or", "is", "for", "on", "with", "as", "by", "be", "it", "this", "that", "are", "can", "you", "how", "do", "use" };

    /// <summary>Lowercase words, plus camelCase parts ("ItemTemplate" gives itemtemplate, item, template).</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in Word().Matches(text))
        {
            var w = m.Value;
            var lower = w.ToLowerInvariant();
            if (lower.Length > 1 && !Stop.Contains(lower)) yield return lower;
            var parts = CamelSplit().Split(w);
            if (parts.Length > 1)
                foreach (var p in parts)
                {
                    var pl = p.ToLowerInvariant();
                    if (pl.Length > 2 && !Stop.Contains(pl)) yield return pl;
                }
        }
    }
}
