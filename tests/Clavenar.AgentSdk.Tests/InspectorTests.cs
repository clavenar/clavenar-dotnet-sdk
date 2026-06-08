namespace Clavenar.AgentSdk.Tests;

using System;
using System.Collections.Generic;
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
}
