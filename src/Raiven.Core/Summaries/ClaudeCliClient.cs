using System.Diagnostics;
using System.Text.Json;
using Raiven.Core.Logging;

namespace Raiven.Core.Summaries;

public sealed class ClaudeCliClient(string modelAlias) : IClaudeClient
{
    private const string ClaudeCliNotFoundMessage = "Could not find the 'claude' CLI. Is Claude Code installed and on PATH?";

    public async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
    {
        string claudePath;
        try
        {
            claudePath = ResolveExecutablePath("claude");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ClaudeCliNotFoundMessage, ex);
        }

        var psi = new ProcessStartInfo
        {
            FileName = claudePath,
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
            throw new InvalidOperationException(ClaudeCliNotFoundMessage, ex);
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

    internal static string ResolveExecutablePath(string command)
    {
        // Win32 CreateProcess (which Process.Start uses under UseShellExecute=false)
        // does not do the PATHEXT-based extension search a shell prompt does — it
        // needs the exact filename. Claude Code on Windows is an npm-installed
        // .cmd/.ps1 shim, not a bare .exe, so a plain FileName = "claude" fails with
        // "the system cannot find the file specified" even though `claude` runs fine
        // from a terminal. Resolve the real file ourselves, honoring PATHEXT order,
        // and hand Process.Start the exact resolved path.
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in searchPaths)
        {
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate)) return candidate;
            }
            var bare = Path.Combine(dir, command);
            if (File.Exists(bare)) return bare;
        }

        throw new InvalidOperationException($"Could not find '{command}' on PATH.");
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
