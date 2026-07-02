namespace Raiven.Core.Transcripts;

public sealed record TurnSlice(string UserPrompt, string AssistantText, IReadOnlyList<string> ToolsUsed);
