using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NoesisMcp.Docs;

namespace NoesisMcp.Tools;

[McpServerToolType]
public sealed class DocsTools(DocsIndex index)
{
    [McpServerTool(Name = "search_docs", Title = "Search the NoesisGUI docs", ReadOnly = true, Idempotent = true)]
    [Description("Searches a local copy of the official NoesisGUI documentation (tutorials, Studio docs, API notes). Returns up to max_results " +
                 "matches, each with title, section, URL and a snippet of up to 500 characters. Use it before writing XAML for a feature you are " +
                 "unsure Noesis supports, e.g. 'ListBox ItemTemplate', 'Storyboard Loaded', 'gamepad focus navigation'.")]
    public string SearchDocs(
        [Description("What to look for, in keywords, e.g. 'DataTrigger binding'.")] string query,
        [Description("Maximum number of results, 1 to 10. Default 5.")] int max_results = 5)
    {
        if (index.Count == 0)
            return "The docs index is not built. Run: python scripts/fetch-docs.py (writes data/docs.json), then restart the server.";
        var hits = index.Search(query, max_results);
        if (hits.Count == 0) return $"No results for '{query}'. Try fewer or different keywords, or call list_controls.";
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
        {
            var c = hits[i].Chunk;
            var title = string.IsNullOrEmpty(c.Section) || c.Section == c.Title ? c.Title : $"{c.Title} › {c.Section}";
            sb.AppendLine($"{i + 1}. {title}");
            sb.AppendLine($"   {c.Url}");
            sb.AppendLine($"   {DocsIndex.Snippet(c, query)}");
        }
        return sb.ToString().TrimEnd();
    }
}
