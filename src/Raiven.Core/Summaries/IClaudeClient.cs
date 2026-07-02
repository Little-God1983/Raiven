namespace Raiven.Core.Summaries;

public interface IClaudeClient
{
    Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default);
}
