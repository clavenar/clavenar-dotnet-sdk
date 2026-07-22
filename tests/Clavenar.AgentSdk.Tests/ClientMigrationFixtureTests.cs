namespace Clavenar.AgentSdk.Tests;

using System;
using System.IO;
using System.Text.Json.Nodes;

public sealed class ClientMigrationFixtureTests
{
    [Fact]
    public void PackagesExplicitDecisionMigrationBoundary()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "client-migration-v1.fixture.json")))!.AsObject();
        JsonObject schema = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "client-migration-v1.schema.json")))!.AsObject();

        Assert.Equal("clavenar.client-migration/v1", (string?)fixture["contract"]);
        Assert.Equal("1.5.0", (string?)fixture["minimumSafeVersions"]!["dotnet"]);
        Assert.Equal(426, (int)fixture["legacyRejection"]!["httpStatus"]!);
        Assert.False((bool)fixture["legacyRejection"]!["executable"]!);
        Assert.Equal(0, (int)fixture["legacyRejection"]!["toolEffectCount"]!);
        Assert.True((bool)fixture["invariants"]!["legacyInspectionCannotExecute"]!);
        Assert.Equal(
            (string?)fixture["contract"],
            (string?)schema["properties"]!["contract"]!["const"]);
    }
}
