using Anthropic;
using Anthropic.Models.Messages;

namespace Raiven.Core.Summaries;

public sealed class AnthropicClaudeClient(string model) : IClaudeClient
{
    private readonly AnthropicClient _client = new();

    public async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 300,
            System = systemPrompt,
            Messages = [new() { Role = Role.User, Content = userContent }],
        };

        var response = await _client.Messages.Create(parameters).WaitAsync(ct);

        var text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Claude returned an empty response.");
        return text;
    }
}
