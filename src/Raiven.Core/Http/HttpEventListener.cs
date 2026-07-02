using System.Net;
using System.Text;
using Raiven.Core.Events;
using Raiven.Core.Logging;

namespace Raiven.Core.Http;

public sealed class HttpEventListener(int port, Func<RaivenEvent, Task> onEvent) : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleRequestAsync(context));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                await WriteAsync(context, 405, """{"error":"POST only"}""");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            RaivenEvent evt;
            try
            {
                evt = EventParser.Parse(body);
            }
            catch (FormatException)
            {
                await WriteAsync(context, 400, """{"error":"unrecognized event"}""");
                return;
            }

            // Respond immediately; never block the sender (Claude Code) on our handling.
            await WriteAsync(context, 200, """{"ok":true}""");

            _ = Task.Run(async () =>
            {
                try
                {
                    await onEvent(evt);
                }
                catch (Exception ex)
                {
                    FileLog.Error($"Event handler failed for {evt.Source}/{evt.Type}", ex);
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Error("Request handling failed", ex);
            try { context.Response.Abort(); } catch { /* already gone */ }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); } catch { /* not started */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* shutdown */ }
        }
        ((IDisposable)_listener).Dispose();
        _cts.Dispose();
    }
}
