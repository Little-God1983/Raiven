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
        foreach (var arg in BuildArguments(systemPrompt, modelAlias))
            psi.ArgumentList.Add(arg);

        ScrubChildEnvironment(psi.Environment);

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

    // Strip environment variables that, when inherited from RAIVEN's own process,
    // make the child `claude` CLI misbehave in non-interactive mode.
    //   - ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN: Claude Code prefers these over the
    //     /login subscription credential, so strip them to force subscription billing.
    //   - CLAUDECODE: set inside every Claude Code session. The CLI refuses to launch
    //     ("Claude Code cannot be launched inside another Claude Code session") whenever
    //     it sees this, so if RAIVEN itself is launched from within a Claude Code session
    //     (a dev `dotnet run`, or install/testing from a Claude Code terminal) every
    //     summary fails until it's cleared. The nesting guard keys on CLAUDECODE alone;
    //     the other CLAUDE_CODE_* vars are tolerated, so leave them be.
    internal static void ScrubChildEnvironment(IDictionary<string, string?> environment)
    {
        environment.Remove("ANTHROPIC_API_KEY");
        environment.Remove("ANTHROPIC_AUTH_TOKEN");
        environment.Remove("CLAUDECODE");
    }

    internal static IReadOnlyList<string> BuildArguments(string systemPrompt, string modelAlias) =>
    [
        "-p",
        "Respond now, based on the content provided on standard input.",
        "--system-prompt",
        systemPrompt,
        "--model",
        modelAlias,
        "--output-format",
        "json",
        "--max-turns",
        "1",
        "--tools",
        "",
        // Load no settings files (user/project/local): RAIVEN's summarizer must run
        // hermetically, or the user's own Stop hook fires on this subprocess and
        // every summary triggers another finished-turn event - an infinite loop
        // once auto-play is enabled. Subscription OAuth auth is unaffected.
        "--setting-sources",
        "",
    ];

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
