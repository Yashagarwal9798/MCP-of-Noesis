using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoesisMcp.Worker;

/// <summary>One JSON line from the server to the Noesis worker.</summary>
public sealed record WorkerRequest
{
    public int Id { get; init; }
    public string Op { get; init; } = "";          // validate | render | probe | ping
    public string? Xaml { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public double Time { get; init; } = 1.0;
    public string? Theme { get; init; }
    public string? SampleData { get; init; }
    public string[]? Names { get; init; }
}

/// <summary>One JSON line back. Worker lines start with "@@" so stray native output can't be mistaken for a reply.</summary>
public sealed record WorkerResponse
{
    public int Id { get; init; }
    public bool Ok { get; init; }
    public int ErrorCount { get; init; }
    public string Text { get; init; } = "";
    public string? PngBase64 { get; init; }
    public double Ms { get; init; }
    public string? Adapter { get; init; }
    public string? Version { get; init; }
    public Dictionary<string, bool>? Probe { get; init; }
    public bool Crashed { get; init; }
}

public static class WorkerJson
{
    public const string Prefix = "@@";
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
