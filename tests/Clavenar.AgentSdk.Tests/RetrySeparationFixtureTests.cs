namespace Clavenar.AgentSdk.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

public sealed class RetrySeparationFixtureTests
{
    [Fact]
    public void ClassifiesOnlyTheDecisionAsAutomaticallyRetryable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "retry-separation-v1.fixture.json");
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonArray cases = fixture["cases"]!.AsArray();
        JsonObject[] automaticallyRetryable = cases
            .Select(item => item!.AsObject())
            .Where(item => (bool)item["automaticTransportRetry"]!)
            .ToArray();

        Assert.Equal("clavenar.retry-separation/v1", (string?)fixture["contract"]);
        Assert.Contains(
            automaticallyRetryable,
            item => (string?)item["id"] == "explicit-side-effect-free-decision");
        Assert.DoesNotContain(
            automaticallyRetryable,
            item => (int)item["maximumEffectAttempts"]! > 0);

        JsonObject executor = cases
            .Select(item => item!.AsObject())
            .Single(item => (string?)item["id"] == "sdk-registered-executor");
        Assert.False((bool)executor["automaticTransportRetry"]!);
        Assert.Equal(1, (int)executor["maximumEffectAttempts"]!);
    }
}
