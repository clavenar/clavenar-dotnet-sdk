namespace Clavenar.AgentSdk.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public class InspectorTests
{
    private static NormalizedToolCall Call(string id, string name) =>
        NormalizedToolCall.FromJsonArguments(id, name, "{}");

    [Fact]
    public async Task EmptyIsNoop()
    {
        var inspector = new ClavenarInspector(Fixtures.Opts(new StubHandler((_, _) => StubResponse.Of(200))));
        await inspector.InspectAllAsync(Array.Empty<NormalizedToolCall>());
    }

    [Fact]
    public async Task OrderedFirstDeny()
    {
        var h = new StubHandler((_, body) => new StubResponse
        {
            Status = 403,
            Body = "{\"error\":\"x\",\"reasons\":[\"no\"]}",
            Delay = Fixtures.ToolName(body) == "slow_deny" ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero,
        });
        var calls = new[] { Call("1", "slow_deny"), Call("2", "fast_deny") };
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectAllAsync(calls));
        Assert.Equal("slow_deny", e.ToolName);
    }

    [Fact]
    public async Task EnforceTransportErrorNoPolicyCallback()
    {
        bool fired = false;
        var h = new StubHandler((_, _) => StubResponse.Of(500));
        var opts = Fixtures.Opts(h) with
        {
            OnPolicyError = (_, _, _) =>
            {
                fired = true;
                return Task.CompletedTask;
            },
        };
        await Assert.ThrowsAsync<ClavenarTransportException>(
            () => new ClavenarInspector(opts).InspectAllAsync(new[] { Fixtures.SampleCall() }));
        Assert.False(fired);
    }

    [Fact]
    public async Task ObservePassesThroughDeny()
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
        await new ClavenarInspector(opts).InspectAllAsync(new[] { Fixtures.SampleCall() });
        Assert.Equal(new[] { VerdictKind.Deny }, kinds);
    }

    [Fact]
    public async Task ObserveTransportTreatedAllow()
    {
        int pol = 0;
        var h = new StubHandler((_, _) => StubResponse.Of(500));
        var opts = Fixtures.Opts(h) with
        {
            Mode = Mode.Observe,
            OnPolicyError = (_, _, _) =>
            {
                pol++;
                return Task.CompletedTask;
            },
        };
        await new ClavenarInspector(opts).InspectAllAsync(new[] { Fixtures.SampleCall() });
        Assert.Equal(1, pol);
    }

    [Fact]
    public async Task PendingEnforce()
    {
        var h = new StubHandler((_, _) => new StubResponse
        {
            Status = 202,
            Body = "{\"status\":\"pending\",\"correlation_id\":\"cp\",\"review_reasons\":[\"needs review\"]}",
            CorrelationId = "cp",
        });
        var e = await Assert.ThrowsAsync<ClavenarPendingException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectAllAsync(new[] { Fixtures.SampleCall() }));
        Assert.Equal("cp", e.CorrelationId);
        Assert.Equal("delete_user", e.ToolName);
    }

    [Fact]
    public async Task RateLimitedEnforce()
    {
        const string body =
            "{\"verdict\":\"rate_limited\",\"layer\":\"proxy\",\"error\":\"rate_limited\",\"reasons\":[\"agent request velocity exceeded\"],\"retry_after_secs\":30}";
        var h = new StubHandler((_, _) => new StubResponse { Status = 429, Body = body, CorrelationId = "c-429" });
        var e = await Assert.ThrowsAsync<ClavenarRateLimitedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectAllAsync(new[] { Fixtures.SampleCall() }));
        Assert.Equal("delete_user", e.ToolName);
        Assert.Equal("rate_limited", e.Code);
        Assert.Equal(new[] { "agent request velocity exceeded" }, e.Reasons);
        Assert.Equal(30, e.RetryAfterSecs);
        Assert.Equal("proxy", e.Layer);
        Assert.Equal("c-429", e.CorrelationId);
        Assert.Equal("clavenar rate_limited for tool \"delete_user\" (retry after 30s)", e.Message);
    }

    [Fact]
    public async Task QuotaExceededEnforce()
    {
        const string body =
            "{\"verdict\":\"quota_exceeded\",\"layer\":\"proxy\",\"error\":\"quota_exceeded\",\"reasons\":[\"tenant monthly spend cap reached\"]}";
        var h = new StubHandler((_, _) => StubResponse.Of(429, body));
        var e = await Assert.ThrowsAsync<ClavenarRateLimitedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).EnforceAsync("delete_user", "call_1", "{}"));
        Assert.Equal("quota_exceeded", e.Code);
        Assert.Null(e.RetryAfterSecs);
        Assert.Equal("clavenar quota_exceeded for tool \"delete_user\"", e.Message);
    }

    [Fact]
    public async Task ObservePassesThroughRateLimited()
    {
        var kinds = new List<VerdictKind>();
        const string body =
            "{\"verdict\":\"rate_limited\",\"error\":\"rate_limited\",\"reasons\":[\"agent request velocity exceeded\"],\"retry_after_secs\":30}";
        var h = new StubHandler((_, _) => StubResponse.Of(429, body));
        var opts = Fixtures.Opts(h) with
        {
            Mode = Mode.Observe,
            OnVerdict = (v, _, _) =>
            {
                kinds.Add(v.Kind);
                return Task.CompletedTask;
            },
        };
        await new ClavenarInspector(opts).InspectAllAsync(new[] { Fixtures.SampleCall() });
        Assert.Equal(new[] { VerdictKind.RateLimited }, kinds);
    }

    [Fact]
    public async Task OnVerdictErrorPropagates()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var opts = Fixtures.Opts(h) with
        {
            OnVerdict = (_, _, _) => throw new InvalidOperationException("stop"),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ClavenarInspector(opts).InspectAllAsync(new[] { Fixtures.SampleCall() }));
    }

    [Fact]
    public async Task EnforceConvenience()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\",\"reasons\":[\"no\"]}"));
        await Assert.ThrowsAsync<ClavenarDeniedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).EnforceAsync("delete_user", "call_1", "{\"u\":1}"));
    }

    [Fact]
    public async Task InspectResponseAnthropic()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\",\"reasons\":[\"no\"]}"));
        object resp = new
        {
            content = new object[]
            {
                new { type = "text", text = "hi" },
                new { type = "tool_use", id = "toolu_1", name = "delete_user", input = new { user = "alice" } },
            },
        };
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectResponseAsync(resp));
        Assert.Equal("delete_user", e.ToolName);
    }

    [Fact]
    public async Task InspectResponseOpenAi()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(403, "{\"error\":\"x\",\"reasons\":[\"no\"]}"));
        object resp = new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        tool_calls = new[]
                        {
                            new { id = "call_1", type = "function", function = new { name = "delete_user", arguments = "{\"u\":1}" } },
                        },
                    },
                },
            },
        };
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectResponseAsync(resp));
        Assert.Equal("delete_user", e.ToolName);
    }

    // Provider-shape drift: the turn declares tool use but the blocks aren't extractable. The
    // call must pass through without hitting the gateway (fail-open by contract) while the
    // shape-drift trace warning fires.
    [Fact]
    public async Task InspectResponseWarnsWhenToolUseDeclaredButNothingExtracted()
    {
        var h = new StubHandler((_, _) => throw new InvalidOperationException(
            "gateway must not be contacted when extraction yields zero calls"));
        object resp = new
        {
            stop_reason = "tool_use",
            content = new object[]
            {
                new { type = "tool_use_v2", id = "toolu_1", name = "delete_user", input = new { } },
            },
        };
        var listener = new CollectingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await new ClavenarInspector(Fixtures.Opts(h)).InspectResponseAsync(resp);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains(listener.Messages, m => m.Contains("may have drifted"));
    }

    [Theory]
    [InlineData("{\"stop_reason\":\"tool_use\"}", true)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\"}]}", true)]
    [InlineData("{\"stop_reason\":\"end_turn\"}", false)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\"}]}", false)]
    [InlineData("{}", false)]
    public void DeclaresToolUseMatchesProviderStopMarkers(string json, bool expected)
    {
        Assert.Equal(expected, ClavenarInspector.DeclaresToolUse(JsonNode.Parse(json)));
    }

    private sealed class CollectingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }
    }
}
