namespace Clavenar.AgentSdk.Tests;

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class TransportTests
{
    private static Task<Verdict> Inspect(ClavenarOptions opts) =>
        new ClavenarInspector(opts).InspectAsync(Fixtures.SampleCall());

    [Fact]
    public async Task Allow()
    {
        var h = new StubHandler((_, _) => new StubResponse { Status = 200, CorrelationId = "c1" });
        var v = await Inspect(Fixtures.Opts(h));
        Assert.Equal(VerdictKind.Allow, v.Kind);
        Assert.Equal("c1", v.CorrelationId);
    }

    [Fact]
    public async Task Deny()
    {
        const string body =
            "{\"error\":\"security_violation\",\"reasons\":[\"nope\"],\"review_reasons\":[\"r\"],\"intent_category\":\"Destruction\",\"layer\":\"policy\"}";
        var h = new StubHandler((_, _) => new StubResponse { Status = 403, Body = body, CorrelationId = "c1" });
        var v = await Inspect(Fixtures.Opts(h));
        Assert.Equal(VerdictKind.Deny, v.Kind);
        Assert.Equal(new[] { "nope" }, v.Reasons);
        Assert.Equal("Destruction", v.IntentCategory);
        Assert.Equal("policy", v.Layer);
        Assert.Equal("c1", v.CorrelationId);
    }

    [Fact]
    public async Task DenyNormalization()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\"}"));
        var v = await Inspect(Fixtures.Opts(h));
        Assert.Empty(v.Reasons);
        Assert.Empty(v.ReviewReasons);
        Assert.Equal(string.Empty, v.IntentCategory);
        Assert.Null(v.Layer);
    }

    [Fact]
    public async Task DenyBadShapeIsTransport()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"foo\":1}"));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Inspect(Fixtures.Opts(h)));
        Assert.Equal(403, e.Status);
    }

    [Fact]
    public async Task PendingHeaderWins()
    {
        const string body = "{\"status\":\"pending\",\"correlation_id\":\"cb\",\"review_reasons\":[\"x\"]}";
        var h = new StubHandler((_, _) => new StubResponse { Status = 202, Body = body, CorrelationId = "ch" });
        var v = await Inspect(Fixtures.Opts(h));
        Assert.Equal(VerdictKind.Pending, v.Kind);
        Assert.Equal("ch", v.CorrelationId);
        Assert.Equal(new[] { "x" }, v.ReviewReasons);
    }

    [Fact]
    public async Task PendingBodyFallback()
    {
        const string body = "{\"status\":\"pending\",\"correlation_id\":\"cb\",\"review_reasons\":[]}";
        var h = new StubHandler((_, _) => StubResponse.Of(202, body));
        var v = await Inspect(Fixtures.Opts(h));
        Assert.Equal("cb", v.CorrelationId);
    }

    [Fact]
    public async Task PendingBothEmpty()
    {
        const string body = "{\"status\":\"pending\",\"correlation_id\":\"\",\"review_reasons\":[]}";
        var h = new StubHandler((_, _) => StubResponse.Of(202, body));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Inspect(Fixtures.Opts(h)));
        Assert.Equal(202, e.Status);
        Assert.Contains("missing correlation id", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedStatus()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(500, "boom"));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Inspect(Fixtures.Opts(h)));
        Assert.Equal(500, e.Status);
    }

    [Fact]
    public async Task NetworkError()
    {
        var h = new StubHandler((_, _) => new StubResponse { Throw = true });
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Inspect(Fixtures.Opts(h)));
        Assert.Equal(0, e.Status);
    }

    [Fact]
    public async Task Timeout()
    {
        var h = new StubHandler((_, _) => new StubResponse { Status = 200, Delay = TimeSpan.FromSeconds(5) });
        var opts = Fixtures.Opts(h) with { Timeout = TimeSpan.FromMilliseconds(40) };
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Inspect(opts));
        Assert.Contains("timed out", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryThenSuccess()
    {
        int n = 0;
        var h = new StubHandler((_, _) => ++n < 3 ? StubResponse.Of(503) : StubResponse.Of(200));
        var v = await Inspect(Fixtures.Opts(h) with { Retry = new RetryOptions(3, TimeSpan.FromMilliseconds(1)) });
        Assert.Equal(VerdictKind.Allow, v.Kind);
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task NoRetryOn4xx()
    {
        int n = 0;
        var h = new StubHandler((_, _) =>
        {
            n++;
            return StubResponse.Of(400);
        });
        await Assert.ThrowsAsync<ClavenarTransportException>(
            () => Inspect(Fixtures.Opts(h) with { Retry = new RetryOptions(3, TimeSpan.FromMilliseconds(1)) }));
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task MaxAttemptsBelowOne()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(
            () => Inspect(Fixtures.Opts(h) with { Retry = new RetryOptions(0, TimeSpan.FromMilliseconds(1)) }));
        Assert.Contains("MaxAttempts", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestEnvelope()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var h = new StubHandler((req, body) =>
        {
            captured = req;
            capturedBody = body;
            return StubResponse.Of(200);
        });
        await new ClavenarInspector(Fixtures.Opts(h) with { Token = "tok" }).InspectAsync(Fixtures.SampleCall());

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/mcp", captured.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer tok", captured.Headers.GetValues("Authorization").First());

        var env = JsonNode.Parse(capturedBody!)!;
        Assert.Equal("2.0", (string?)env["jsonrpc"]);
        Assert.Equal("tools/call", (string?)env["method"]);
        Assert.Equal("toolu_1", (string?)env["id"]);
        Assert.Equal("delete_user", (string?)env["params"]!["name"]);
        Assert.Equal("alice", (string?)env["params"]!["arguments"]!["user"]);
    }

    [Fact]
    public void JoinUrl()
    {
        Assert.Equal("http://x/mcp", Transport.JoinUrl("http://x/", "/mcp"));
        Assert.Equal("http://x/mcp", Transport.JoinUrl("http://x", "mcp"));
        Assert.Equal("https://gw/clavenar/mcp", Transport.JoinUrl("https://gw/clavenar", "/mcp"));
    }

    [Fact]
    public void ConfigErrorOnBadEndpoint()
    {
        Assert.Throws<ClavenarConfigException>(() => new ClavenarInspector(new ClavenarOptions { Endpoint = "" }));
        Assert.Throws<ClavenarConfigException>(() => new ClavenarInspector(new ClavenarOptions { Endpoint = "not-a-url" }));
    }
}
