using Clavenar.AgentSdk;

// The provider-agnostic pattern — no provider SDK. Build NormalizedToolCalls from your framework's
// tool-dispatch boundary (a Semantic Kernel filter, a custom loop) and inspect before running them.
var inspector = new ClavenarInspector(new ClavenarOptions
{
    Endpoint = Environment.GetEnvironmentVariable("CLAVENAR_ENDPOINT") ?? "http://localhost:8088",
    Token = Environment.GetEnvironmentVariable("CLAVENAR_LITE_TOKEN"),
});

var calls = new[]
{
    NormalizedToolCall.FromJsonArguments("call_1", "delete_user", "{\"user\":\"alice\"}"),
};

try
{
    await inspector.InspectAllAsync(calls);
    Console.WriteLine($"cleared {calls.Length} tool call(s) — dispatch them");
}
catch (ClavenarDeniedException d)
{
    Console.WriteLine($"blocked {d.ToolName}: {string.Join(", ", d.Reasons)}");
}
catch (ClavenarPendingException p)
{
    Console.WriteLine($"parked {p.ToolName} for review; waiting for an operator...");
    await p.ResolveAsync();
    Console.WriteLine($"approved — dispatch {p.ToolName}");
}
