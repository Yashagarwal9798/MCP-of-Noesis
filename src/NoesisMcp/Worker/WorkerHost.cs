using System.Text;
using System.Text.Json;
using NoesisMcp.Engine;

namespace NoesisMcp.Worker;

/// <summary>
/// Child-process side (<c>NoesisMcp --worker</c>). Owns Noesis and the D3D11 device on a single
/// thread and answers one JSON request per line on stdin.
/// </summary>
public static class WorkerHost
{
    public static int Run()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        using var engine = new NoesisEngine();
        engine.Init();
        Console.Error.WriteLine($"[worker] Noesis {engine.Version} ready (licensed: {engine.Licensed})");

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            WorkerResponse response;
            WorkerRequest? req = null;
            try
            {
                req = JsonSerializer.Deserialize<WorkerRequest>(line, WorkerJson.Options)!;
                response = Handle(engine, req);
            }
            catch (Exception e)
            {
                response = new WorkerResponse { Id = req?.Id ?? 0, Ok = false, ErrorCount = 1, Text = "Internal error: " + e.Message };
            }
            stdout.WriteLine(WorkerJson.Prefix + JsonSerializer.Serialize(response, WorkerJson.Options));
        }
        return 0;
    }

    static WorkerResponse Handle(NoesisEngine engine, WorkerRequest req)
    {
        switch (req.Op)
        {
            case "ping":
                return new WorkerResponse { Id = req.Id, Ok = true, Text = "pong", Version = engine.Version };

            case "validate":
            {
                var v = engine.Validate(req.Xaml ?? "");
                return new WorkerResponse { Id = req.Id, Ok = v.Ok, ErrorCount = v.Errors.Count(), Text = v.Format() };
            }

            case "render":
            {
                RenderResult r;
                try { r = engine.Render(req.Xaml ?? "", req.Width, req.Height, req.Time, req.Theme, req.SampleData); }
                catch (ArgumentException e) { return new WorkerResponse { Id = req.Id, Ok = false, ErrorCount = 1, Text = e.Message }; }

                if (r.Png == null)
                    return new WorkerResponse { Id = req.Id, Ok = false, ErrorCount = r.Validation.Errors.Count(), Text = "Not rendered. " + r.Validation.Format() };
                var warnings = r.Validation.Warnings.ToList();
                var text = $"Rendered {r.Width}x{r.Height} (root: {r.Validation.RootType}) in {r.Milliseconds:F0} ms on {r.Adapter}, t={req.Time:0.##}s, theme={req.Theme ?? "DarkBlue"}.";
                if (warnings.Count > 0) text += "\n" + string.Join("\n", warnings.Select(w => w.ToString()));
                return new WorkerResponse
                {
                    Id = req.Id, Ok = true, Text = text, PngBase64 = Convert.ToBase64String(r.Png),
                    Ms = r.Milliseconds, Adapter = r.Adapter,
                };
            }

            case "probe":
                return new WorkerResponse { Id = req.Id, Ok = true, Probe = engine.ProbeElements(req.Names ?? []) };

            default:
                return new WorkerResponse { Id = req.Id, Ok = false, ErrorCount = 1, Text = $"Unknown op '{req.Op}'" };
        }
    }
}
