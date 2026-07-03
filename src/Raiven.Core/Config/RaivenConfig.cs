using System.Text.Json;

namespace Raiven.Core.Config;

public sealed class RaivenConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public int Port { get; init; } = 9876;
    public string Voice { get; init; } = "af_heart";
    public string? ChimeWavPath { get; init; }
    public string Model { get; init; } = "claude-haiku-4-5";
    public int MaxTranscriptChars { get; init; } = 30000;
    public int SessionExpiryMinutes { get; init; } = 240;
    public string SummaryBackend { get; init; } = "cli"; // "cli" (Claude subscription via CLI) or "api" (pay-per-token Anthropic API)
    public string CliModelAlias { get; init; } = "haiku"; // CLI model alias: sonnet, opus, haiku, or fable

    public static RaivenConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new RaivenConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Options));
            return defaults;
        }

        try
        {
            return JsonSerializer.Deserialize<RaivenConfig>(File.ReadAllText(path)) ?? new RaivenConfig();
        }
        catch (JsonException)
        {
            return new RaivenConfig();
        }
    }
}
