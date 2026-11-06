using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoesisMcp.Docs;
using NoesisMcp.Worker;

// Two modes in one executable:
//   NoesisMcp            -> MCP server over stdio (what Claude Code launches)
//   NoesisMcp --worker   -> Noesis worker child process (launched by the server)
if (args.Contains("--worker"))
    return WorkerHost.Run();

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol. Every log line goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<NoesisWorkerClient>();
builder.Services.AddSingleton(_ => DocsIndex.LoadDefault());
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "noesis", Version = "0.1.0" };
        o.ServerInstructions =
            "Tools for writing NoesisGUI XAML (game UI middleware; a subset of WPF XAML). Workflow: search_docs/list_controls when unsure " +
            "a feature exists -> write XAML -> validate_xaml until OK -> render_xaml to check the result visually.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Warm the Noesis worker in the background so the first tool call is fast.
var worker = app.Services.GetRequiredService<NoesisWorkerClient>();
_ = Task.Run(async () =>
{
    try { await worker.CallAsync(new WorkerRequest { Op = "ping" }, TimeSpan.FromSeconds(30)); }
    catch (Exception e) { Console.Error.WriteLine("[noesis-mcp] worker warm-up failed: " + e.Message); }
});

var docs = app.Services.GetRequiredService<DocsIndex>();
Console.Error.WriteLine(docs.Count > 0
    ? $"[noesis-mcp] docs index: {docs.Count} chunks from {docs.SourcePath}"
    : "[noesis-mcp] docs index not built (run scripts/fetch-docs.py); search_docs will say so.");

await app.RunAsync();
return 0;
