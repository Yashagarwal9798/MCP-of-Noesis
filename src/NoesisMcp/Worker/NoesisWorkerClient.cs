using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NoesisMcp.Worker;

/// <summary>
/// Server side of the worker. Runs one Noesis worker process and sends it one request at a time
/// (Noesis is single-threaded). Restarts the worker when:
///   - it crashes (native C++ crashes can't be caught in .NET),
///   - a call times out (e.g. runaway layout), or
///   - it gets close to Noesis's 10-minute evaluation limit (only when no license is set).
/// </summary>
public sealed class NoesisWorkerClient : IAsyncDisposable
{
    readonly ILogger<NoesisWorkerClient> _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly TimeSpan _recycleAfter;
    Process? _proc;
    DateTime _startedUtc;
    int _nextId;

    public int Restarts { get; private set; }

    public NoesisWorkerClient(ILogger<NoesisWorkerClient> log)
    {
        _log = log;
        var licensed = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY"));
        var env = Environment.GetEnvironmentVariable("NOESIS_MCP_RECYCLE_SECONDS");
        _recycleAfter = int.TryParse(env, out var s) && s > 0 ? TimeSpan.FromSeconds(s)
            : licensed ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMinutes(9); // trial views stop rendering after 10 minutes
    }

    public async Task<WorkerResponse> CallAsync(WorkerRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_proc != null && _recycleAfter != Timeout.InfiniteTimeSpan && DateTime.UtcNow - _startedUtc > _recycleAfter)
            {
                _log.LogInformation("Recycling Noesis worker (evaluation-mode time limit).");
                Kill();
            }
            if (_proc == null || _proc.HasExited) Start();

            var req = request with { Id = Interlocked.Increment(ref _nextId) };
            await _proc!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req, WorkerJson.Options).AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var line = await _proc.StandardOutput.ReadLineAsync(cts.Token);
                    if (line == null) return Crash(request, "The Noesis worker exited while handling this XAML (native crash). It has been restarted; simplify the XAML and try again.");
                    if (!line.StartsWith(WorkerJson.Prefix, StringComparison.Ordinal)) { _log.LogDebug("worker: {Line}", line); continue; }
                    var resp = JsonSerializer.Deserialize<WorkerResponse>(line.AsSpan(WorkerJson.Prefix.Length), WorkerJson.Options)!;
                    if (resp.Id == req.Id) return resp;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Crash(request, $"Noesis did not finish within {timeout.TotalSeconds:0} s (possible infinite layout or animation loop). The worker has been restarted.");
            }
        }
        finally { _gate.Release(); }
    }

    WorkerResponse Crash(WorkerRequest request, string message)
    {
        Kill();
        Restarts++;
        _log.LogWarning("Noesis worker failed on op {Op}: {Message}", request.Op, message);
        return new WorkerResponse { Id = request.Id, Ok = false, ErrorCount = 1, Text = message, Crashed = true };
    }

    void Start()
    {
        var (file, args) = SelfCommand();
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // worker logs go straight to our stderr, never to MCP stdout
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--worker");
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the Noesis worker.");
        _startedUtc = DateTime.UtcNow;
        _log.LogInformation("Started Noesis worker pid {Pid}", _proc.Id);
    }

    /// <summary>How to launch this same program: the apphost exe, or "dotnet NoesisMcp.dll".</summary>
    static (string File, string[] Args) SelfCommand()
    {
        var exe = Environment.ProcessPath!;
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, [typeof(NoesisWorkerClient).Assembly.Location]);
        return (exe, []);
    }

    void Kill()
    {
        if (_proc == null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _proc.Dispose();
        _proc = null;
    }

    public ValueTask DisposeAsync()
    {
        try { _proc?.StandardInput.Close(); _proc?.WaitForExit(2000); } catch { }
        Kill();
        return ValueTask.CompletedTask;
    }
}
