using System.Net;
using System.Net.Sockets;
using System.Text;
using Raiven.Core.Events;
using Raiven.Core.Http;

namespace Raiven.Core.Tests;

public class HttpEventListenerTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Post_ValidStopPayload_Returns200AndInvokesHandler()
    {
        var port = GetFreePort();
        var received = new TaskCompletionSource<RaivenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new HttpEventListener(port, evt =>
        {
            received.TrySetResult(evt);
            return Task.CompletedTask;
        });
        listener.Start();

        var body = """{"session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","hook_event_name":"Stop"}""";
        var response = await Http.PostAsync($"http://127.0.0.1:{port}/events",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Stop", evt.Type);
        Assert.Equal("claude-code", evt.Source);
    }

    [Fact]
    public async Task Post_Garbage_Returns400()
    {
        var port = GetFreePort();
        await using var listener = new HttpEventListener(port, _ => Task.CompletedTask);
        listener.Start();

        var response = await Http.PostAsync($"http://127.0.0.1:{port}/events",
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_Returns405()
    {
        var port = GetFreePort();
        await using var listener = new HttpEventListener(port, _ => Task.CompletedTask);
        listener.Start();

        var response = await Http.GetAsync($"http://127.0.0.1:{port}/events");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Post_HandlerThrows_StillReturns200()
    {
        var port = GetFreePort();
        await using var listener = new HttpEventListener(port, _ => throw new InvalidOperationException("handler boom"));
        listener.Start();

        var body = """{"session_id":"s1","transcript_path":"t","cwd":"c","hook_event_name":"Stop"}""";
        var response = await Http.PostAsync($"http://127.0.0.1:{port}/events",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
