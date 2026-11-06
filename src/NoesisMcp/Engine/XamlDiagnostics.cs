using System.Text.RegularExpressions;
using Noesis;

namespace NoesisMcp.Engine;

/// <summary>Turns Noesis log lines into "Line L, Col C: message (hint: ...)" diagnostics.</summary>
public static partial class XamlDiagnostics
{
    // Noesis formats parse errors as "<memory>(line,col): message".
    [GeneratedRegex(@"^<memory>\((\d+),(\d+)\):\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex MemoryLocation();

    [GeneratedRegex(@"^(?<file>[^()]+)\((\d+),(\d+)\):\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex FileLocation();

    public static List<Diagnostic> FromLog(IEnumerable<(LogLevel Level, string Message)> log)
    {
        var list = new List<Diagnostic>();
        foreach (var (level, message) in log)
        {
            var sev = level >= LogLevel.Error ? "error" : "warning";
            var m = MemoryLocation().Match(message);
            if (m.Success)
            {
                var text = m.Groups[3].Value.Trim();
                list.Add(new Diagnostic(sev, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), text, HintFor(text)));
                continue;
            }
            // Errors raised while loading another file (e.g. the theme) are not the user's problem.
            if (FileLocation().IsMatch(message) && message.StartsWith("Theme/", StringComparison.Ordinal)) continue;
            if (message.StartsWith("[TRIAL]", StringComparison.Ordinal)) continue;
            list.Add(new Diagnostic(sev, 0, 0, message.Trim(), HintFor(message)));
        }
        return list;
    }

    public static string? HintFor(string message)
    {
        if (message.StartsWith("Unknown element type", StringComparison.Ordinal))
            return "Not found in Noesis. Check spelling, call list_controls to see supported types, or wpf_differences for WPF-only types.";
        if (message.StartsWith("Unknown member", StringComparison.Ordinal))
            return "This property doesn't exist on that type in Noesis. Call list_controls with the type name to see its properties.";
        if (message.Contains("Default namespace not declared", StringComparison.Ordinal))
            return $"Add xmlns=\"{NoesisEngine.PresentationNs}\" and xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" to the root element.";
        if (message.Contains("StaticResource", StringComparison.Ordinal) && message.Contains("not found", StringComparison.Ordinal))
            return "StaticResource is resolved in document order: define the resource above its first use, or use DynamicResource.";
        if (message.Contains("unable to convert", StringComparison.Ordinal))
            return "The value has the wrong format for this property type.";
        if (message.Contains("Tag mismatch", StringComparison.Ordinal) || message.Contains("not closed", StringComparison.OrdinalIgnoreCase))
            return "Malformed XML: an element isn't closed or is closed in the wrong order. The real cause is often above this line.";
        if (message.Contains("x:Class", StringComparison.Ordinal))
            return "x:Class needs a registered code-behind type. Remove it for previews.";
        if (message.StartsWith("Font not found", StringComparison.Ordinal))
            return "Use a font from the theme (default), or a system font family name such as Arial or Segoe UI.";
        if (message.Contains("Binding", StringComparison.Ordinal) && message.Contains("Command", StringComparison.Ordinal))
            return "Pass the command in sample_data as \"Name\": \"$command\" (a no-op ICommand) so the preview matches the game.";
        if (message.Contains("Binding", StringComparison.Ordinal))
            return "Pass sample_data to render_xaml so bindings resolve in the preview.";
        return null;
    }

    public static List<Diagnostic> Dedupe(IEnumerable<Diagnostic> diags) =>
        diags.GroupBy(d => (d.Severity, d.Line, d.Column, d.Message)).Select(g => g.First())
             .OrderBy(d => d.Severity == "error" ? 0 : 1).ThenBy(d => d.Line).ThenBy(d => d.Column).ToList();
}
