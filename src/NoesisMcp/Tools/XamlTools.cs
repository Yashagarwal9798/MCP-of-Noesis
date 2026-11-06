using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NoesisMcp.Worker;

namespace NoesisMcp.Tools;

[McpServerToolType]
public sealed class XamlTools(NoesisWorkerClient worker)
{
    static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    [McpServerTool(Name = "validate_xaml", Title = "Validate NoesisGUI XAML", ReadOnly = true, Idempotent = true)]
    [Description("Checks NoesisGUI XAML with the real Noesis SDK parser (not WPF). Returns 'OK (root: Type)' or one error per line as " +
                 "'Line L, Col C: message (hint: ...)'. Noesis implements only part of WPF, so ALWAYS call this before showing, saving or " +
                 "rendering XAML, fix every error, and call it again until it returns OK.")]
    public async Task<CallToolResult> ValidateXaml(
        [Description("The complete XAML document, including xmlns declarations on the root element.")] string xaml,
        CancellationToken ct)
    {
        var r = await worker.CallAsync(new WorkerRequest { Op = "validate", Xaml = xaml }, ValidateTimeout, ct);
        return new CallToolResult { IsError = !r.Ok, Content = [new TextContentBlock { Text = r.Text }] };
    }

    [McpServerTool(Name = "render_xaml", Title = "Render NoesisGUI XAML to PNG", ReadOnly = true, Idempotent = true)]
    [Description("Renders NoesisGUI XAML to a PNG with the real Noesis renderer (offscreen D3D11) so you can check the layout visually. " +
                 "Validates first and returns the errors instead of an image if the XAML is invalid. Standard controls use the Noesis theme. " +
                 "Use sample_data so {Binding ...} expressions show real values, and time to capture a frame of a Loaded storyboard.")]
    public async Task<CallToolResult> RenderXaml(
        [Description("The complete XAML document. The root must be a visual element such as Grid, Border, UserControl or Page.")] string xaml,
        [Description("Width in pixels (16 to 3840). Default 1280.")] int width = 1280,
        [Description("Height in pixels (16 to 2160). Default 720.")] int height = 720,
        [Description("Animation time in seconds at which to capture the frame (0 to 60). Default 1.0. Use 0 for the first frame of an intro animation.")] double time = 1.0,
        [Description("Noesis theme for standard controls: DarkBlue (default), DarkAqua, DarkCrimson, DarkEmerald, DarkGreen, DarkLime, DarkOrange, DarkPurple, DarkRed, the Light* variants, or 'none'.")] string theme = "DarkBlue",
        [Description("Optional JSON object used as the root DataContext, e.g. {\"Score\": 7, \"Items\": [{\"Name\": \"Sword\"}], \"PlayCommand\": \"$command\"}. The string \"$command\" becomes a real no-op ICommand, so Command bindings behave as in the game.")] string? sample_data = null,
        [Description("Optional absolute path ending in .png. The image is also written there, e.g. to keep previews next to the XAML.")] string? save_to = null,
        CancellationToken ct = default)
    {
        var r = await worker.CallAsync(new WorkerRequest
        {
            Op = "render", Xaml = xaml, Width = width, Height = height, Time = time, Theme = theme, SampleData = sample_data,
        }, RenderTimeout, ct);

        if (!r.Ok || r.PngBase64 == null)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = r.Text }] };

        var png = Convert.FromBase64String(r.PngBase64);
        var text = r.Text;
        if (!string.IsNullOrWhiteSpace(save_to))
        {
            if (!Path.IsPathFullyQualified(save_to) || !save_to.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                text += "\nNot saved: save_to must be an absolute path ending in .png.";
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(save_to)!);
                await File.WriteAllBytesAsync(save_to, png, ct);
                text += $"\nSaved to {save_to}";
            }
        }
        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = text }],
        };
    }
}
