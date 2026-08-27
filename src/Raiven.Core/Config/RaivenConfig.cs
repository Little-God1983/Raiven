using System.Text.Json;
using Raiven.Core.Logging;

namespace Raiven.Core.Config;

public sealed class RaivenConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public int Port { get; set; } = 9876;
    public string Voice { get; set; } = "af_heart";
    public string? ChimeWavPath { get; set; }
    public string Model { get; set; } = "claude-haiku-4-5";
    public int MaxTranscriptChars { get; set; } = 30000;
    public int SummaryTimeoutSeconds { get; set; } = 120; // max seconds for the summary Claude call; clamped to 1-600 at use
    public int SessionExpiryMinutes { get; set; } = 240;
    public string SummaryBackend { get; set; } = "cli"; // "cli" (Claude subscription via CLI) or "api" (pay-per-token Anthropic API)
    public string CliModelAlias { get; set; } = "haiku"; // CLI model alias: sonnet, opus, haiku, or fable
    public bool AutoPlaySummary { get; set; } = true;
    public int AutoPlayDelaySeconds { get; set; } // 0 = play immediately (no countdown); clamped to 0-60 at use
    public int HistorySize { get; set; } = 10; // Recent summaries kept; clamped to 1-50 at use
    public bool NotifyOnFinishedTurn { get; set; } = true;
    public string FinishedTurnVoice { get; set; } = "summary"; // "summary" (Claude Haiku) or "message" (read last assistant message verbatim)
    public int FinishedTurnWordLimit { get; set; } // spoken word cap for "message" mode; 0 = unlimited
    public bool NotifyOnQuestion { get; set; } = true;
    public string QuestionVoice { get; set; } = "announce"; // "announce", "message" (read the hook's message), or "summary" (Claude Haiku)
    public int QuestionWordLimit { get; set; } // spoken word cap for "message" mode; 0 = unlimited
    public bool SuppressDuplicateAskUserQuestionPrompt { get; set; } = true; // drop Claude Code's "permission to use AskUserQuestion" notification when the PreToolUse hook already announced that dialog
    public bool KeepAudioAlive { get; set; } // continuous silent stream keeps the audio device / Bluetooth link awake
    public bool ShowPlaybackStatus { get; set; } = true; // finished-turn toast stays open showing summarize/generate/speak stages

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

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not save config to {path}", ex);
        }
    }
}
