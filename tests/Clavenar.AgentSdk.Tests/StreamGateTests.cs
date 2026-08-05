namespace Clavenar.AgentSdk.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class StreamGateTests
{
    [Fact]
    public async Task Allow()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Start("0", "toolu_1", "delete_user");
        gate.Update("0", null, null, "{\"user\":");
        gate.Update("0", null, null, "\"alice\"}");
        await gate.CloseAsync("0");
        Assert.False(gate.Has("0"));
    }

    [Fact]
    public async Task Deny()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\",\"reasons\":[\"no\"]}"));
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Start("0", "toolu_1", "delete_user");
        gate.Update("0", null, null, "{}");
        await Assert.ThrowsAsync<ClavenarDeniedException>(() => gate.CloseAsync("0"));
    }

    [Fact]
    public async Task EmptyArgsBecomeObject()
    {
        string? body = null;
        var h = new StubHandler((_, b) =>
        {
            body = b;
            return StubResponse.Of(200);
        });
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Start("0", "toolu_1", "noop");
        await gate.CloseAsync("0");
        var args = JsonNode.Parse(body!)!["params"]!["arguments"]!.AsObject();
        Assert.Empty(args);
    }

    [Fact]
    public async Task UnparseableArgs()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Start("0", "toolu_1", "f");
        gate.Update("0", null, null, "not json");
        await Assert.ThrowsAsync<ClavenarTransportException>(() => gate.CloseAsync("0"));
    }

    [Fact]
    public async Task MissingIdName()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Update("0", null, null, "{\"a\":1}");
        await Assert.ThrowsAsync<ClavenarTransportException>(() => gate.CloseAsync("0"));
    }

    [Fact]
    public async Task BatchOrder()
    {
        var h = new StubHandler((_, b) =>
            StubResponse.Of(403, $"{{\"error\":\"x\",\"reasons\":[\"denied {Fixtures.ToolName(b)}\"]}}"));
        var gate = new StreamGate(Fixtures.Opts(h));
        gate.Update("0:0", "id_a", "first", "{}");
        gate.Update("0:1", "id_b", "second", "{}");
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(() => gate.CloseByPrefixAsync("0:"));
        Assert.Equal("first", e.ToolName);
    }

    [Fact]
    public async Task ObserveDoesNotThrow()
    {
        var kinds = new List<VerdictKind>();
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\",\"reasons\":[\"no\"]}"));
        var opts = Fixtures.Opts(h) with
        {
            Mode = Mode.Observe,
            OnVerdict = (v, _, _) =>
            {
                kinds.Add(v.Kind);
                return Task.CompletedTask;
            },
        };
        var gate = new StreamGate(opts);
        gate.Start("0", "toolu_1", "f");
        gate.Update("0", null, null, "{}");
        await gate.CloseAsync("0");
        Assert.Equal(new[] { VerdictKind.Deny }, kinds);
    }

    [Fact]
    public async Task TerminalWithoutBufferFailsClosed()
    {
        var gate = new StreamGate(Fixtures.Opts(new StubHandler((_, _) => StubResponse.Of(200))));
        await Assert.ThrowsAsync<ClavenarTransportException>(() => gate.CloseAsync("missing"));
    }

    [Fact]
    public async Task TerminalWithoutBufferReportsAndPassesInObserve()
    {
        var errors = new List<string>();
        var opts = Fixtures.Opts(new StubHandler((_, _) => StubResponse.Of(200))) with
        {
            Mode = Mode.Observe,
            OnPolicyError = (error, _, _) =>
            {
                errors.Add(error.Message);
                return Task.CompletedTask;
            },
        };
        await new StreamGate(opts).CloseAsync("missing");
        Assert.Single(errors);
    }
}
