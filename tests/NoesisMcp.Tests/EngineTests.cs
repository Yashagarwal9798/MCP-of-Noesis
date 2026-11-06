using System.Buffers.Binary;
using System.Text.RegularExpressions;
using NoesisMcp.Engine;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NoesisMcp.Tests;

/// <summary>One Noesis instance for the whole run: Noesis is a process-wide singleton.</summary>
public sealed class EngineFixture : IDisposable
{
    public NoesisEngine Engine { get; } = new();
    public EngineFixture() => Engine.Init();
    public void Dispose() => Engine.Dispose();
}

[CollectionDefinition("noesis")]
public sealed class NoesisCollection : ICollectionFixture<EngineFixture>;

public static class Corpus
{
    public static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tests", "xaml"))) return Path.Combine(d.FullName, "tests", "xaml");
        throw new DirectoryNotFoundException("tests/xaml not found");
    }

    public static IEnumerable<object[]> Files(string folder) =>
        Directory.GetFiles(Path.Combine(Root, folder), "*.xaml").Order().Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>Expected results live in the file's first-line comment: &lt;!-- expect: ... --&gt;</summary>
    public static (string Xaml, List<string> Expect) Load(string folder, string name)
    {
        var xaml = File.ReadAllText(Path.Combine(Root, folder, name));
        var expect = Regex.Matches(xaml, @"<!--\s*expect:\s*(.*?)\s*-->").Select(m => m.Groups[1].Value).ToList();
        return (xaml, expect);
    }
}

[Collection("noesis")]
public class CorpusTests(EngineFixture fx)
{
    public static IEnumerable<object[]> Valid => Corpus.Files("valid");
    public static IEnumerable<object[]> Invalid => Corpus.Files("invalid");
    public static IEnumerable<object[]> WpfOnly => Corpus.Files("wpf-only");

    [Theory, MemberData(nameof(Valid))]
    public void Valid_files_validate_ok(string file)
    {
        var (xaml, _) = Corpus.Load("valid", file);
        var r = fx.Engine.Validate(xaml);
        Assert.True(r.Ok, r.Format());
        Assert.StartsWith("OK", r.Format());
    }

    [Theory, MemberData(nameof(Invalid))]
    public void Invalid_files_report_expected_error(string file)
    {
        var (xaml, expect) = Corpus.Load("invalid", file);
        var r = fx.Engine.Validate(xaml);
        Assert.False(r.Ok);
        var text = r.Format();
        foreach (var e in expect) Assert.Contains(e, text);
        Assert.Matches(@"Line \d+, Col \d+: ", text); // one consistent format
    }

    [Theory, MemberData(nameof(WpfOnly))]
    public void Wpf_only_types_are_rejected_with_hint(string file)
    {
        var (xaml, expect) = Corpus.Load("wpf-only", file);
        var r = fx.Engine.Validate(xaml);
        Assert.False(r.Ok);
        foreach (var e in expect) Assert.Contains(e, r.Format());
        Assert.Contains("wpf_differences", r.Format());
    }

    [Theory, MemberData(nameof(Valid))]
    public void Valid_files_render_to_png_of_requested_size(string file)
    {
        var (xaml, _) = Corpus.Load("valid", file);
        var r = fx.Engine.Render(xaml, 640, 360, 1.0, null, """{"Score":3,"Hearts":[1,2,3],"Items":[{"Name":"Sword","Color":"Red"}]}""");
        if (r.Validation.RootType == "ResourceDictionary")
        {
            Assert.Null(r.Png); // no visuals: a clear error instead of a blank image
            Assert.Contains("no visuals", r.Validation.Format());
            return;
        }
        Assert.NotNull(r.Png);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, r.Png![..8]);
        Assert.Equal(640, BinaryPrimitives.ReadInt32BigEndian(r.Png.AsSpan(16)));
        Assert.Equal(360, BinaryPrimitives.ReadInt32BigEndian(r.Png.AsSpan(20)));
    }
}

[Collection("noesis")]
public class EngineBehaviorTests(EngineFixture fx)
{
    const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    [Fact]
    public void Same_input_gives_same_output()
    {
        var xaml = $"<Grid {Ns}><Ellipse Width=\"100\" Height=\"100\" Fill=\"Orange\"/></Grid>";
        var a = fx.Engine.Render(xaml, 200, 200, 0, null, null);
        var b = fx.Engine.Render(xaml, 200, 200, 0, null, null);
        Assert.Equal(a.Png, b.Png);
    }

    [Fact]
    public void Render_actually_draws_pixels()
    {
        var bgra = RenderRaw($"<Grid {Ns} Background=\"#FF0000\"/>", 64, 64);
        // Centre pixel is opaque red (BGRA).
        int i = (32 * 64 + 32) * 4;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, bgra[i..(i + 4)]);
    }

    [Fact]
    public void Offscreen_layers_do_not_clip_this_or_later_frames()
    {
        // Group Opacity makes Noesis render an offscreen layer first. That pass used to leave its
        // scissor rect bound, clipping the main pass (and every later render) to one tile.
        var group = $"<Grid {Ns} Background=\"Navy\"><Canvas Opacity=\"0.5\"><Ellipse Width=\"50\" Height=\"50\" Fill=\"White\"/></Canvas></Grid>";
        var plain = $"<Grid {Ns} Background=\"#00FF00\"/>";
        int corner = ((300 - 2) * 400 + (400 - 2)) * 4; // bottom-right pixel
        var a = RenderRaw(group, 400, 300);
        Assert.Equal(new byte[] { 128, 0, 0, 255 }, a[corner..(corner + 4)]); // navy, BGRA
        var b = RenderRaw(plain, 400, 300);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, b[corner..(corner + 4)]);
    }

    [Fact]
    public void Storyboard_time_changes_the_frame()
    {
        var (xaml, _) = Corpus.Load("valid", "storyboard-scale-in.xaml");
        var start = fx.Engine.Render(xaml, 320, 180, 0, null, null).Png;
        var end = fx.Engine.Render(xaml, 320, 180, 1.0, null, null).Png;
        Assert.NotEqual(start, end);
    }

    [Fact]
    public void Limits_are_enforced()
    {
        var huge = $"<Grid {Ns}>" + new string(' ', NoesisEngine.MaxXamlBytes) + "</Grid>";
        Assert.Contains("limit", fx.Engine.Validate(huge).Format());
        var tooBig = fx.Engine.Render($"<Grid {Ns}/>", 5000, 100, 0, null, null);
        Assert.Null(tooBig.Png);
        Assert.Contains("out of range", tooBig.Validation.Format());
    }

    [Fact]
    public void Bad_sample_data_is_a_clear_error()
    {
        var r = fx.Engine.Render($"<Grid {Ns}/>", 100, 100, 0, null, "{not json");
        Assert.Null(r.Png);
        Assert.Contains("sample_data is not valid JSON", r.Validation.Format());
    }

    [Fact]
    public void Window_root_is_previewed_as_its_content()
    {
        var r = fx.Engine.Render($"<Window {Ns} Background=\"Navy\"><TextBlock Text=\"Hi\"/></Window>", 100, 100, 0, null, null);
        Assert.NotNull(r.Png);
        Assert.Contains("Window root", r.Validation.Format());
    }

    [Fact]
    public void Probe_finds_supported_and_unsupported_types()
    {
        var p = fx.Engine.ProbeElements(["StackPanel", "Viewport3D"]);
        Assert.True(p["StackPanel"]);
        Assert.False(p["Viewport3D"]);
    }

    [Fact]
    public void Unknown_theme_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => fx.Engine.Render($"<Grid {Ns}/>", 64, 64, 0, "Neon", null));
        fx.Engine.SetTheme("DarkBlue");
    }

    byte[] RenderRaw(string xaml, int w, int h)
    {
        var png = fx.Engine.Render(xaml, w, h, 0, null, null).Png!;
        return PngDecode.Bgra(png, w, h);
    }
}

static class PngDecode
{
    /// <summary>Decodes our own unfiltered RGBA PNG back to BGRA (test helper only).</summary>
    public static byte[] Bgra(byte[] png, int w, int h)
    {
        int pos = 8;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var r = raw.ToArray();
        var outp = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = y * (w * 4 + 1) + 1 + x * 4, d = (y * w + x) * 4;
                outp[d] = r[s + 2]; outp[d + 1] = r[s + 1]; outp[d + 2] = r[s]; outp[d + 3] = r[s + 3];
            }
        return outp;
    }
}
