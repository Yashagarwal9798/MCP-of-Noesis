using Noesis;
using NoesisMcp.Docs;
using NoesisMcp.Engine;

namespace NoesisMcp.Tests;

public class DocsIndexTests
{
    static DocsIndex Sample()
    {
        var idx = new DocsIndex();
        idx.Add([
            new DocChunk("ListBox Class", "Remarks", "https://x/ListBox", "A ListBox shows items. Use ItemTemplate with a DataTemplate to customise each item."),
            new DocChunk("Animation Tutorial", "Storyboards", "https://x/AnimTutorial", "A Storyboard groups animations. Start it with BeginStoryboard from an EventTrigger on Loaded."),
            new DocChunk("Changelog", "Version 4.0", "https://x/Changelog", "Fixed ListBox ItemTemplate crash. Storyboard fixes."),
        ]);
        return idx;
    }

    [Fact]
    public void CamelCase_is_split_for_matching()
    {
        var tokens = DocsIndex.Tokenize("ItemTemplate").ToList();
        Assert.Contains("itemtemplate", tokens);
        Assert.Contains("item", tokens);
        Assert.Contains("template", tokens);
    }

    [Fact]
    public void Best_page_ranks_first_and_changelog_is_demoted()
    {
        var idx = Sample();
        Assert.Equal("https://x/ListBox", idx.Search("ListBox ItemTemplate", 3)[0].Chunk.Url);
        Assert.Equal("https://x/AnimTutorial", idx.Search("storyboard", 3)[0].Chunk.Url);
    }

    [Fact]
    public void No_match_returns_empty()
    {
        Assert.Empty(Sample().Search("zzzqqq", 5));
    }

    [Fact]
    public void Snippets_are_capped_at_500_chars()
    {
        var c = new DocChunk("T", "S", "u", string.Join(" ", Enumerable.Repeat("word", 400)) + " needle " + string.Join(" ", Enumerable.Repeat("word", 400)));
        var s = DocsIndex.Snippet(c, "needle");
        Assert.True(s.Length <= 502, $"length {s.Length}");
        Assert.Contains("needle", s);
    }
}

public class DiagnosticFormatTests
{
    [Fact]
    public void Noesis_log_lines_become_line_col_errors_with_hints()
    {
        var d = XamlDiagnostics.FromLog([(LogLevel.Error, "<memory>(2,3): Unknown element type 'Buton'")]);
        Assert.Single(d);
        Assert.StartsWith("Line 2, Col 3: Unknown element type 'Buton' (hint: ", d[0].ToString());
    }

    [Fact]
    public void Theme_and_trial_messages_are_not_reported_as_user_errors()
    {
        var d = XamlDiagnostics.FromLog([
            (LogLevel.Error, "Theme/NoesisTheme.Fonts.xaml(6,5): Font not found 'Fonts/#PT Root UI'"),
            (LogLevel.Warning, "[TRIAL] No license detected."),
        ]);
        Assert.Empty(d);
    }

    [Fact]
    public void Duplicates_are_removed_and_errors_come_first()
    {
        var list = XamlDiagnostics.Dedupe([
            new Diagnostic("warning", 1, 1, "w", null),
            new Diagnostic("error", 5, 1, "e", null),
            new Diagnostic("error", 5, 1, "e", null),
        ]);
        Assert.Equal(2, list.Count);
        Assert.Equal("error", list[0].Severity);
    }

    [Fact]
    public void Png_encoder_writes_valid_header()
    {
        var png = Png.Encode(new byte[4 * 4 * 4], 4, 4);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
    }
}
