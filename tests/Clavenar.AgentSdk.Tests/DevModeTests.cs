namespace Clavenar.AgentSdk.Tests;

using System;
using System.Threading.Tasks;

public class DevModeTests
{
    [Fact]
    public void RendersDetectorBreakdownWhenDetailPresent()
    {
        var detail = new VerdictDetail(
            new[]
            {
                new DetectorScore("persona_drift", 0.12, false),
                new DetectorScore("injection", 0.91, true),
            },
            new[] { "injection" });
        var d = new ClavenarDeniedException(
            "send_email",
            new[] { "indirect prompt injection" },
            Array.Empty<string>(),
            "Exfiltration",
            "brain",
            "abc-123",
            detail);

        var p = DevMode.RenderDenyPanel(d);
        Assert.Contains("send_email", p);
        Assert.Contains("layer=brain", p);
        Assert.Contains("correlation=abc-123", p);
        Assert.Contains("0.91", p);
        Assert.Contains("⚠ flagged", p);
        Assert.Contains("degraded: injection", p);
    }

    [Fact]
    public void HintsToEnableVerboseVerdictsWhenDetailAbsent()
    {
        var d = new ClavenarDeniedException(
            "wire_transfer", new[] { "policy denied" }, Array.Empty<string>(), "Direct Execution", null, null);
        var p = DevMode.RenderDenyPanel(d);
        Assert.Contains("wire_transfer", p);
        Assert.Contains("CLAVENAR_PROXY_VERBOSE_VERDICTS", p);
        Assert.DoesNotContain("⚠ flagged", p);
    }

    [Fact]
    public async Task ParsesDetailFromDenyBody()
    {
        var h = new StubHandler((_, _) => new StubResponse
        {
            Status = 403,
            Body =
                "{\"error\":\"x\",\"reasons\":[\"no\"],\"detail\":{\"detectors\":[{\"detector\":\"injection\",\"score\":0.9,\"flagged\":true}],\"degraded\":[\"injection\"]}}",
        });
        var e = await Assert.ThrowsAsync<ClavenarDeniedException>(
            () => new ClavenarInspector(Fixtures.Opts(h)).InspectAllAsync(
                new[] { NormalizedToolCall.FromJsonArguments("1", "t", "{}") }));
        Assert.NotNull(e.Detail);
        Assert.Single(e.Detail!.Detectors);
        Assert.True(e.Detail.Detectors[0].Flagged);
        Assert.Equal("injection", e.Detail.Detectors[0].Detector);
    }
}
