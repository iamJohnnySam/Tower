using System.Net;
using System.Net.Http;
using System.Text;
using Tower.Core.Pi;

namespace Tower.Core.Tests;

/// <summary>
/// Stub HttpMessageHandler that returns a pre-canned response (or throws).
/// </summary>
file sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_respond(request));
    }
}

/// <summary>
/// Stub handler that always throws a TaskCanceledException (simulates unreachable host).
/// </summary>
file sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new TaskCanceledException("simulated timeout");
}

public class PiAgentClientTests
{
    const string BaseUrl = "http://atomtv:8889";

    // 1. StatsAsync parses a valid JSON response and exposes expected fields.
    [Fact]
    public async Task StatsAsync_parses_json_and_exposes_fields()
    {
        const string json = """{"hostname":"atomtv","idle":42}""";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        var client = new PiAgentClient(new HttpClient(handler));
        var node = await client.StatsAsync(BaseUrl);

        Assert.NotNull(node);
        Assert.Equal("atomtv", node!["hostname"]!.GetValue<string>());
        Assert.Equal(42, node!["idle"]!.GetValue<int>());
    }

    // 2a. StatsAsync returns null when the handler throws (unreachable pi).
    [Fact]
    public async Task StatsAsync_returns_null_when_handler_throws()
    {
        var client = new PiAgentClient(new HttpClient(new ThrowingHandler()));
        var node = await client.StatsAsync(BaseUrl);
        Assert.Null(node);
    }

    // 2b. StatsAsync returns null when server returns 500.
    [Fact]
    public async Task StatsAsync_returns_null_when_server_returns_500()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            });

        var client = new PiAgentClient(new HttpClient(handler));
        // A 500 with non-JSON body causes JsonNode.Parse to throw → null
        var node = await client.StatsAsync(BaseUrl);
        Assert.Null(node);
    }

    // 2c. ShutdownAsync returns false when handler throws.
    [Fact]
    public async Task ShutdownAsync_returns_false_when_handler_throws()
    {
        var client = new PiAgentClient(new HttpClient(new ThrowingHandler()));
        var result = await client.ShutdownAsync(BaseUrl);
        Assert.False(result);
    }

    // 2d. ShutdownAsync returns false when server returns 500.
    [Fact]
    public async Task ShutdownAsync_returns_false_when_server_returns_500()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var client = new PiAgentClient(new HttpClient(handler));
        var result = await client.ShutdownAsync(BaseUrl);
        Assert.False(result);
    }

    // 3. The URL hit by StatsAsync is exactly {baseUrl}/api/stats.
    [Fact]
    public async Task StatsAsync_hits_correct_url()
    {
        const string json = """{"hostname":"atomtv"}""";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        var client = new PiAgentClient(new HttpClient(handler));
        await client.StatsAsync(BaseUrl);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal($"{BaseUrl}/api/stats", handler.LastRequest!.RequestUri!.ToString());
    }

    // Bonus: ShutdownAsync returns true on 200 OK.
    [Fact]
    public async Task ShutdownAsync_returns_true_on_success()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new PiAgentClient(new HttpClient(handler));
        var result = await client.ShutdownAsync(BaseUrl);
        Assert.True(result);
    }

    // Bonus: LogsAsync returns plain text on success.
    [Fact]
    public async Task LogsAsync_returns_text_on_success()
    {
        const string logText = "2026-06-15 boot ok\n2026-06-15 idle timer reset";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(logText, Encoding.UTF8, "text/plain")
            });

        var client = new PiAgentClient(new HttpClient(handler));
        var result = await client.LogsAsync(BaseUrl);
        Assert.Equal(logText, result);
    }

    // Bonus: LogsAsync returns null when handler throws.
    [Fact]
    public async Task LogsAsync_returns_null_when_handler_throws()
    {
        var client = new PiAgentClient(new HttpClient(new ThrowingHandler()));
        var result = await client.LogsAsync(BaseUrl);
        Assert.Null(result);
    }
}
