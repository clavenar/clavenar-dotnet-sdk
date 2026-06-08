namespace Clavenar.AgentSdk.Tests;

using System;
using System.Net.Http;
using System.Text.Json.Nodes;

internal static class Fixtures
{
    public static ClavenarOptions Opts(StubHandler handler) =>
        new()
        {
            Endpoint = "http://clavenar.test",
            HttpClient = new HttpClient(handler),
            Retry = new RetryOptions(1, TimeSpan.FromMilliseconds(1)),
            Timeout = TimeSpan.FromSeconds(2),
        };

    public static NormalizedToolCall SampleCall() =>
        NormalizedToolCall.FromJsonArguments("toolu_1", "delete_user", "{\"user\":\"alice\"}");

    public static string ToolName(string body)
    {
        try
        {
            return (string?)JsonNode.Parse(body)?["params"]?["name"] ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }
}
