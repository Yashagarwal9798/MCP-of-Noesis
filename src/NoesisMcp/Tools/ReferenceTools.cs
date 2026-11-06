using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NoesisMcp.Worker;

namespace NoesisMcp.Tools;

[McpServerToolType]
public sealed class ReferenceTools(NoesisWorkerClient worker)
{
    static readonly Assembly NoesisGui = typeof(Noesis.FrameworkElement).Assembly;
    static readonly Assembly NoesisApp = typeof(global::NoesisApp.Application).Assembly;
    static string? _wpfCache;
    static readonly SemaphoreSlim WpfLock = new(1, 1);

    static readonly (string Group, Type Base)[] Groups =
    {
        ("Panels", typeof(Noesis.Panel)),
        ("Controls", typeof(Noesis.Control)),
        ("Shapes", typeof(Noesis.Shape)),
        ("Text inlines", typeof(Noesis.Inline)),
        ("Other elements", typeof(Noesis.FrameworkElement)),
        ("Brushes", typeof(Noesis.Brush)),
        ("Geometry", typeof(Noesis.Geometry)),
        ("Transforms", typeof(Noesis.Transform)),
        ("3D transforms", typeof(Noesis.Transform3D)),
        ("Effects", typeof(Noesis.Effect)),
        ("Animation", typeof(Noesis.Timeline)),
        ("Styling and triggers", typeof(Noesis.TriggerBase)),
        ("Markup extensions", typeof(Noesis.MarkupExtension)),
    };

    [McpServerTool(Name = "list_controls", Title = "List Noesis types and properties", ReadOnly = true, Idempotent = true)]
    [Description("Lists the XAML types that exist in the installed NoesisGUI SDK, read from the SDK itself. With type_name, lists that type's " +
                 "dependency properties (own and inherited) and attached properties. Use this when unsure whether a control or property exists in Noesis.")]
    public string ListControls(
        [Description("Optional exact type name, e.g. 'TextBlock' or 'ListBox', to list its properties.")] string? type_name = null,
        [Description("Optional case-insensitive substring filter on type names, e.g. 'Brush'.")] string? filter = null)
    {
        var types = XamlTypes().ToList();
        if (!string.IsNullOrWhiteSpace(type_name))
        {
            var t = types.FirstOrDefault(x => x.Name.Equals(type_name, StringComparison.OrdinalIgnoreCase))
                    ?? AllNoesisTypes().FirstOrDefault(x => x.Name.Equals(type_name, StringComparison.OrdinalIgnoreCase));
            if (t == null)
            {
                var close = types.Where(x => x.Name.Contains(type_name, StringComparison.OrdinalIgnoreCase) ||
                                             type_name.Contains(x.Name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).Take(8);
                return $"'{type_name}' does not exist in NoesisGUI {Noesis.GUI.GetBuildVersion()}. Similar: {string.Join(", ", close)}";
            }
            return DescribeType(t);
        }

        var sb = new StringBuilder($"NoesisGUI XAML types (xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\")");
        sb.AppendLine(filter != null ? $", filter '{filter}':" : ":");
        var used = new HashSet<Type>();
        foreach (var (group, baseType) in Groups)
        {
            var members = types.Where(t => baseType.IsAssignableFrom(t) && !used.Contains(t))
                               .Where(t => filter == null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(t => t.Name).ToList();
            foreach (var m in members) used.Add(m);
            if (members.Count > 0) sb.AppendLine($"{group}: {string.Join(", ", members.Select(m => m.Name))}");
        }
        var interactivity = NoesisApp.GetExportedTypes()
            .Where(t => t.Namespace == "Noesis.Interactivity" && !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
            .Where(t => filter == null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        if (interactivity.Count > 0)
            sb.AppendLine($"Interactivity (xmlns:b=\"http://schemas.microsoft.com/xaml/behaviors\", e.g. <b:Interaction.Triggers>): {string.Join(", ", interactivity)}");
        sb.Append("Call list_controls with type_name for the properties of one type.");
        return sb.ToString();
    }

    static IEnumerable<Type> AllNoesisTypes() => NoesisGui.GetExportedTypes().Where(t => t.Namespace == "Noesis" && !t.IsNested);

    static IEnumerable<Type> XamlTypes() => AllNoesisTypes().Where(t =>
        !t.IsAbstract && !t.IsGenericType && t.IsClass &&
        typeof(Noesis.DependencyObject).IsAssignableFrom(t) || typeof(Noesis.MarkupExtension).IsAssignableFrom(t) && !t.IsAbstract)
        .Where(t => t.GetConstructor(Type.EmptyTypes) != null);

    static string DescribeType(Type t)
    {
        var sb = new StringBuilder();
        var chain = new List<string>();
        for (var b = t; b != null && b != typeof(object); b = b.BaseType) chain.Add(b.Name);
        sb.AppendLine($"{t.Name} ({string.Join(" → ", chain)})");

        var cp = t.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "ContentPropertyAttribute");
        var contentName = cp?.GetType().GetProperty("Name")?.GetValue(cp) as string;
        if (contentName != null) sb.AppendLine($"Content property: {contentName}");

        for (var b = t; b != null && b != typeof(object) && b.Namespace == "Noesis"; b = b.BaseType)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var dps = b.GetFields(flags).Where(f => f.FieldType == typeof(Noesis.DependencyProperty)).Select(f => f.Name)
                       .Concat(b.GetProperties(flags).Where(p => p.PropertyType == typeof(Noesis.DependencyProperty)).Select(p => p.Name))
                       .Where(n => n.EndsWith("Property")).Select(n => n[..^"Property".Length]).Distinct().ToList();
            var attached = dps.Where(n => b.GetMethod("Get" + n, BindingFlags.Public | BindingFlags.Static) != null).ToHashSet();
            var plain = dps.Where(n => !attached.Contains(n)).OrderBy(n => n).ToList();
            var events = b.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(e => e.Name).OrderBy(n => n).ToList();
            if (plain.Count == 0 && attached.Count == 0 && events.Count == 0) continue;
            sb.Append($"[{b.Name}]");
            if (plain.Count > 0) sb.Append($" properties: {string.Join(", ", plain)}.");
            if (attached.Count > 0) sb.Append($" attached: {string.Join(", ", attached.Order().Select(a => b.Name + "." + a))}.");
            if (events.Count > 0) sb.Append($" events: {string.Join(", ", events)}.");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // WPF element types that UI agents commonly reach for. Each one is checked against the real
    // Noesis parser, so the answer reflects the installed SDK rather than anyone's memory.
    static readonly string[] WpfElements =
    {
        "Border", "Button", "Calendar", "Canvas", "CheckBox", "ComboBox", "ContentControl", "ContextMenu", "DataGrid", "DatePicker",
        "DockPanel", "DocumentViewer", "Expander", "FlowDocumentReader", "FlowDocumentScrollViewer", "Frame", "Grid", "GridSplitter",
        "GroupBox", "Image", "InkCanvas", "ItemsControl", "Label", "ListBox", "ListView", "MediaElement", "Menu", "MenuItem", "Page",
        "PasswordBox", "Popup", "ProgressBar", "RadioButton", "RepeatButton", "RichTextBox", "ScrollBar", "ScrollViewer", "Separator",
        "Slider", "StackPanel", "StatusBar", "TabControl", "TextBlock", "TextBox", "ToggleButton", "ToolBar", "ToolTip", "TreeView",
        "UniformGrid", "UserControl", "Viewbox", "Viewport3D", "VirtualizingStackPanel", "WebBrowser", "Window", "WrapPanel",
        "AccessText", "Ellipse", "Line", "Path", "Polygon", "Polyline", "Rectangle", "Glyphs", "MultiBinding", "PriorityBinding",
        "DropShadowEffect", "BlurEffect", "VisualBrush", "DrawingBrush", "ImageBrush", "LinearGradientBrush", "RadialGradientBrush",
        "BitmapImage", "DrawingImage", "FormattedText", "Hyperlink", "Run", "Span", "Bold", "Italic", "Underline", "LineBreak",
        "InlineUIContainer", "Paragraph", "FlowDocument", "Table", "Storyboard", "DoubleAnimationUsingKeyFrames", "ColorAnimation",
        "ThicknessAnimation", "PointAnimation", "PointAnimationUsingPath", "MatrixAnimationUsingPath", "StringAnimationUsingKeyFrames",
        "ObjectAnimationUsingKeyFrames", "VisualStateManager", "DataTemplate", "HierarchicalDataTemplate", "ItemsPanelTemplate",
        "ControlTemplate", "CollectionViewSource", "CompositeCollection", "AdornerDecorator", "Viewport2DVisual3D", "ModelVisual3D",
    };

    const string Notes = """
        Notes that matter when porting WPF habits to Noesis:
        - Root element: games normally load a UserControl, Page or Grid into a Noesis View; NoesisApp desktop samples also use Window.
        - x:Class needs a registered code-behind type in the game. Leave it out for previews.
        - Behaviors and triggers such as InvokeCommandAction, EventTrigger and DataTrigger (Interactivity) come from xmlns:b="http://schemas.microsoft.com/xaml/behaviors".
        - Images and fonts are resolved by the game's texture and font providers, so relative URIs depend on how the game loads files.
        - The renderer is vector-based on the GPU. Prefer vector shapes and brushes over large bitmaps for crisp UI at any resolution.
        - Noesis adds features WPF lacks: 3D transforms on any element (Transform3D), text stroke, and gamepad/keyboard focus navigation tuned for games.
        """;

    [McpServerTool(Name = "wpf_differences", Title = "WPF vs Noesis differences", ReadOnly = true, Idempotent = true)]
    [Description("Reports which common WPF element types are NOT available in the installed NoesisGUI SDK (checked live against the Noesis parser), " +
                 "plus short porting notes. Call this when coming from WPF/WinUI/MAUI habits or after an 'Unknown element type' error.")]
    public async Task<CallToolResult> WpfDifferences(CancellationToken ct)
    {
        await WpfLock.WaitAsync(ct);
        try
        {
            if (_wpfCache == null)
            {
                var r = await worker.CallAsync(new WorkerRequest { Op = "probe", Names = WpfElements }, TimeSpan.FromSeconds(60), ct);
                if (!r.Ok || r.Probe == null)
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = r.Text }] };
                var missing = r.Probe.Where(kv => !kv.Value).Select(kv => kv.Key).Order().ToList();
                var present = r.Probe.Count(kv => kv.Value);
                _wpfCache = $"Checked {r.Probe.Count} common WPF element types against the Noesis {Noesis.GUI.GetBuildVersion()} parser: " +
                            $"{present} are available.\nNOT available in Noesis ({missing.Count}): {string.Join(", ", missing)}\n\n{Notes}";
            }
            return new CallToolResult { Content = [new TextContentBlock { Text = _wpfCache }] };
        }
        finally { WpfLock.Release(); }
    }
}
