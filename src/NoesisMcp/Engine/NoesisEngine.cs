using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Noesis;

namespace NoesisMcp.Engine;

/// <summary>
/// The only code that touches the Noesis SDK. Not thread-safe: call it from one thread.
/// In the MCP server it lives inside the worker process (see Worker/), so a native crash
/// or the evaluation-mode time limit never takes the server down.
/// </summary>
public sealed class NoesisEngine : IDisposable
{
    public const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public const int MaxXamlBytes = 200 * 1024;
    public const int MaxWidth = 3840, MaxHeight = 2160;

    public static readonly string[] Themes =
    {
        "DarkBlue", "DarkAqua", "DarkCrimson", "DarkEmerald", "DarkGreen", "DarkLime", "DarkOrange", "DarkPurple", "DarkRed",
        "LightBlue", "LightAqua", "LightCrimson", "LightEmerald", "LightGreen", "LightLime", "LightOrange", "LightPurple", "LightRed",
    };

    readonly List<(LogLevel Level, string Message)> _capture = new();
    bool _capturing;
    string? _theme;
    D3DOffscreen? _d3d;
    bool _initialized;

    public string Version { get; private set; } = "";
    public bool Licensed { get; private set; }

    public void Init()
    {
        if (_initialized) return;
        Log.SetLogCallback(OnLog);
        Error.SetUnhandledCallback(ex => OnLog(LogLevel.Error, "", "Unhandled: " + ex.Message));
        GUI.DisableInspector();
        GUI.DisableHotReload();

        var name = Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        var key = Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(key)) { GUI.SetLicense(name, key); Licensed = true; }

        GUI.Init();
        Version = GUI.GetBuildVersion();

        // NoesisApp 4.0.1's Theme reports Namespace "NoesisApp" but its fonts are embedded as
        // "Theme.Fonts.*", so the theme's own font provider can't find "Fonts/#PT Root UI".
        // A fallback provider with an empty namespace resolves "Theme/Fonts/..." correctly.
        var themeAsm = typeof(NoesisApp.Theme).Assembly;
        NoesisApp.Application.SetThemeProviders(null, new NoesisApp.EmbeddedFontProvider(themeAsm, ""), null);
        SetTheme("DarkBlue");
        _initialized = true;
    }

    void OnLog(LogLevel level, string channel, string message)
    {
        if (_capturing && level >= LogLevel.Warning) _capture.Add((level, message));
        if (level >= LogLevel.Warning) Console.Error.WriteLine($"[noesis {level}] {message}");
    }

    public void SetTheme(string? theme)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "DarkBlue" : theme;
        if (theme == _theme) return;
        if (theme.Equals("none", StringComparison.OrdinalIgnoreCase))
            GUI.SetApplicationResources(null);
        else
        {
            var match = Themes.FirstOrDefault(t => t.Equals(theme, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown theme '{theme}'. Use one of: {string.Join(", ", Themes)}, none");
            GUI.LoadApplicationResources($"Theme/NoesisTheme.{match}.xaml");
        }
        _theme = theme;
    }

    /// <summary>Parses XAML with the real Noesis parser and returns its diagnostics.</summary>
    public ValidationResult Validate(string xaml)
    {
        var limit = CheckSize(xaml);
        if (limit != null) return ValidationResult.Fail(limit);

        var (root, diags) = Parse(xaml);
        var errors = diags.Where(d => d.Severity == "error").ToList();
        if (root == null && errors.Count == 0)
            errors.Add(new Diagnostic("error", 1, 1, "Noesis could not parse the document (no root object).", null));
        return new ValidationResult(errors.Count == 0, root?.GetType().Name, diags);
    }

    (object? Root, List<Diagnostic> Diags) Parse(string xaml)
    {
        _capture.Clear();
        _capturing = true;
        object? root = null;
        string? exception = null;
        try { root = GUI.ParseXaml(xaml); }
        catch (Exception e) { exception = e.Message; }
        finally { _capturing = false; }

        var diags = XamlDiagnostics.FromLog(_capture);
        if (exception != null) diags.Add(new Diagnostic("error", 1, 1, exception, null));
        return (root, XamlDiagnostics.Dedupe(diags));
    }

    /// <summary>Validates, then renders offscreen through Noesis's D3D11 render device.</summary>
    public RenderResult Render(string xaml, int width, int height, double time, string? theme, string? sampleDataJson)
    {
        var limit = CheckSize(xaml);
        if (limit != null) return RenderResult.Fail(new ValidationResult(false, null, new() { limit }));
        if (width < 16 || height < 16 || width > MaxWidth || height > MaxHeight)
            return RenderResult.Fail(new ValidationResult(false, null, new()
            { new Diagnostic("error", 0, 0, $"Size {width}x{height} out of range (16x16 to {MaxWidth}x{MaxHeight}).", null) }));
        if (time < 0 || time > 60) time = Math.Clamp(time, 0, 60);

        SetTheme(theme);
        var (root, diags) = Parse(xaml);
        var errors = diags.Where(d => d.Severity == "error").ToList();
        if (errors.Count > 0 || root == null)
        {
            if (root == null && errors.Count == 0) diags.Add(new Diagnostic("error", 1, 1, "Noesis could not parse the document.", null));
            return RenderResult.Fail(new ValidationResult(false, root?.GetType().Name, diags));
        }

        if (root is not FrameworkElement element)
            return RenderResult.Fail(new ValidationResult(false, root.GetType().Name, new()
            { new Diagnostic("error", 1, 1, $"Root is {root.GetType().Name}, which has no visuals. Render needs a visual root such as Grid, Border or UserControl.", null) }));

        // A Window needs a native display to show itself. Preview its content in a Border instead.
        if (element is ContentControl window && element.GetType().Name == "Window")
        {
            var content = window.Content as UIElement;
            var background = window.Background;
            var resources = window.Resources;
            window.Content = null;
            element = new Border { Background = background, Child = content, Resources = resources };
            diags.Add(new Diagnostic("warning", 1, 1, "Window root: previewing its content (a Window needs a native display).", null));
        }

        if (!string.IsNullOrWhiteSpace(sampleDataJson))
        {
            try { element.DataContext = SampleData.Build(JsonNode.Parse(sampleDataJson)); }
            catch (Exception e)
            {
                return RenderResult.Fail(new ValidationResult(false, root.GetType().Name, new()
                { new Diagnostic("error", 0, 0, "sample_data is not valid JSON: " + e.Message, null) }));
            }
        }

        _d3d ??= new D3DOffscreen();
        var sw = Stopwatch.StartNew();
        _capture.Clear();
        _capturing = true;
        byte[] png;
        try
        {
            var bgra = _d3d.Render(element, width, height, time);
            png = Png.Encode(bgra, width, height);
        }
        finally { _capturing = false; }
        sw.Stop();

        var runtime = XamlDiagnostics.FromLog(_capture).Select(d => d with { Severity = "warning" });
        diags.AddRange(runtime);
        return new RenderResult(png, width, height, sw.Elapsed.TotalMilliseconds, _d3d.Adapter,
            new ValidationResult(true, root.GetType().Name, XamlDiagnostics.Dedupe(diags)));
    }

    /// <summary>Asks the real parser which element types exist. Used by wpf_differences.</summary>
    public Dictionary<string, bool> ProbeElements(IEnumerable<string> names)
    {
        var result = new Dictionary<string, bool>();
        foreach (var name in names)
        {
            var (_, diags) = Parse($"<Grid xmlns=\"{PresentationNs}\"><{name}/></Grid>");
            result[name] = !diags.Any(d => d.Message.StartsWith("Unknown element type", StringComparison.Ordinal));
        }
        return result;
    }

    static Diagnostic? CheckSize(string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml)) return new Diagnostic("error", 0, 0, "XAML is empty.", null);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(xaml);
        return bytes > MaxXamlBytes
            ? new Diagnostic("error", 0, 0, $"XAML is {bytes / 1024} KB; the limit is {MaxXamlBytes / 1024} KB. Split it into smaller files.", null)
            : null;
    }

    public void Dispose()
    {
        _d3d?.Dispose();
        if (_initialized) GUI.Shutdown();
    }
}

public sealed record Diagnostic(string Severity, int Line, int Column, string Message, string? Hint)
{
    public override string ToString()
    {
        var where = Line > 0 ? $"Line {Line}, Col {Column}: " : "";
        var sev = Severity == "warning" ? "warning: " : "";
        return Hint == null ? $"{where}{sev}{Message}" : $"{where}{sev}{Message} (hint: {Hint})";
    }
}

public sealed record ValidationResult(bool Ok, string? RootType, List<Diagnostic> Diagnostics)
{
    public static ValidationResult Fail(Diagnostic d) => new(false, null, new() { d });
    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == "error");
    public IEnumerable<Diagnostic> Warnings => Diagnostics.Where(d => d.Severity != "error");

    public string Format()
    {
        var lines = new List<string>();
        var errors = Errors.ToList();
        var warnings = Warnings.ToList();
        lines.Add(Ok
            ? $"OK (root: {RootType ?? "?"}){(warnings.Count > 0 ? $", {warnings.Count} warning(s)" : "")}"
            : $"{errors.Count} error(s){(warnings.Count > 0 ? $", {warnings.Count} warning(s)" : "")}:");
        lines.AddRange(errors.Select(e => e.ToString()));
        lines.AddRange(warnings.Select(w => w.ToString()));
        return string.Join("\n", lines);
    }
}

public sealed record RenderResult(byte[]? Png, int Width, int Height, double Milliseconds, string? Adapter, ValidationResult Validation)
{
    public static RenderResult Fail(ValidationResult v) => new(null, 0, 0, 0, null, v);
}
