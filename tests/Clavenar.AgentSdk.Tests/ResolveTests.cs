namespace Clavenar.AgentSdk.Tests;

using System;
using System.Threading.Tasks;

public class ResolveTests
{
    private static ClavenarPendingException Pending(StubHandler h)
    {
        var opts = Fixtures.Opts(h);
        return new ClavenarPendingException(
            "delete_user", "c1", new[] { "needs review" },
            ct => Transport.PollPendingOnceAsync("c1", opts, ct));
    }

    private static ResolveOptions Fast() => new(TimeSpan.FromMilliseconds(2), TimeSpan.FromSeconds(2));

    private static string View(string? decision, string? note)
    {
        string d = decision is null ? "null" : $"\"{decision}\"";
        string n = note is null ? "null" : $"\"{note}\"";
        return "{\"correlation_id\":\"c1\",\"agent_id\":\"a\",\"tool_type\":\"shell\",\"method\":\"tools/call\","
            + "\"review_reasons\":[],\"requested_at\":\"2026-01-01T00:00:00Z\",\"decided_at\":null,"
            + $"\"decision\":{d},\"decider_note\":{n}}}";
    }

    [Fact]
    public async Task AllowAfterPolls()
    {
        int n = 0;
        var h = new StubHandler((_, _) => StubResponse.Of(200, ++n < 3 ? View(null, null) : View("allow", null)));
        await Pending(h).ResolveAsync(Fast());
    }

    [Fact]
    public async Task DenyWithNote()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200, View("deny", "too risky")));
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(() => Pending(h).ResolveAsync(Fast()));
        Assert.Equal("PendingDenied", e.IntentCategory);
        Assert.Equal(new[] { "too risky" }, e.Reasons);
        Assert.Equal("c1", e.CorrelationId);
    }

    [Fact]
    public async Task DenyNoNote()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200, View("deny", null)));
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(() => Pending(h).ResolveAsync(Fast()));
        Assert.Equal(new[] { "operator denied" }, e.Reasons);
    }

    [Fact]
    public async Task Terminal404()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(404));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(() => Pending(h).ResolveAsync(Fast()));
        Assert.Equal(404, e.Status);
    }

    [Fact]
    public async Task Malformed200IsTerminal()
    {
        int polls = 0;
        var h = new StubHandler((_, _) =>
        {
            polls++;
            return StubResponse.Of(200, "{}");
        });
        var error = await Assert.ThrowsAsync<ClavenarTransportException>(
            () => Pending(h).ResolveAsync(Fast()));
        Assert.Equal(200, error.Status);
        Assert.Equal(1, polls);
    }

    [Fact]
    public async Task Swallows5xxThenAllow()
    {
        int n = 0;
        var h = new StubHandler((_, _) => ++n < 3 ? StubResponse.Of(502) : StubResponse.Of(200, View("allow", null)));
        await Pending(h).ResolveAsync(Fast());
    }

    [Fact]
    public async Task Deadline()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200, View(null, null)));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(
            () => Pending(h).ResolveAsync(new ResolveOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30))));
        Assert.Contains("not decided within", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadInterval()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200, View(null, null)));
        var e = await Assert.ThrowsAsync<ClavenarTransportException>(
            () => Pending(h).ResolveAsync(new ResolveOptions(TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(1))));
        Assert.Contains("PollInterval", e.Message, StringComparison.Ordinal);
    }
}
