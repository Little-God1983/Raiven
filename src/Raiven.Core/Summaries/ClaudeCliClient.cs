using System.Diagnostics;
using System.Text.Json;

namespace Raiven.Core.Summaries;

public sealed class ClaudeCliClient(string modelAlias) : IClaudeClient
{
    public async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("Respond now, based on the content provided on standard input.");
        psi.ArgumentList.Add("--system-prompt");
        psi.ArgumentList.Add(systemPrompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(modelAlias);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");

        // Force subscription billing: if ANTHROPIC_API_KEY/ANTHROPIC_AUTH_TOKEN are set in
        // RAIVEN's own environment, Claude Code prefers them over the /login subscription
        // credential in non-interactive mode. Strip them so the CLI falls through to the
        // subscription OAuth credential instead.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Could not launch the 'claude' CLI. Is Claude Code installed and on PATH?", ex);
        }

        await process.StandardInput.WriteAsync(userContent);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"claude CLI exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return ExtractResultText(stdout);
    }

    public static string ExtractResultText(string stdoutJson)
    {
        using var doc = JsonDocument.Parse(stdoutJson);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("claude CLI output did not contain a 'result' string field.");

        var text = result.GetString()!.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("claude CLI returned an empty result.");
        return text;
    }
}
