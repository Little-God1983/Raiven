using System.Diagnostics;
using System.Text.Json;
using Raiven.Core.Logging;

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

        var writeTask = WriteStdinAsync(process, userContent, ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await Task.WhenAll(writeTask, process.WaitForExitAsync(ct));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { FileLog.Error("Failed to kill claude CLI subprocess after cancellation", ex); }
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

    private static async Task WriteStdinAsync(Process process, string userContent, CancellationToken ct)
    {
        var bytes = process.StandardInput.Encoding.GetBytes(userContent);
        await process.StandardInput.BaseStream.WriteAsync(bytes, ct);
        await process.StandardInput.BaseStream.FlushAsync(ct);
        process.StandardInput.Close();
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
