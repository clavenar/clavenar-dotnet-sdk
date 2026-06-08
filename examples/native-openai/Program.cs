using Clavenar.AgentSdk;
using OpenAI.Chat;

var inspector = new ClavenarInspector(new ClavenarOptions
{
    Endpoint = Environment.GetEnvironmentVariable("CLAVENAR_ENDPOINT") ?? "http://localhost:8088",
    Token = Environment.GetEnvironmentVariable("CLAVENAR_LITE_TOKEN"),
});

var client = new ChatClient(model: "gpt-4o", apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
ChatCompletion completion = (await client.CompleteChatAsync("delete the alice user")).Value;

// The OpenAI .NET model flattens tool calls onto the completion; inspect each before dispatch.
foreach (ChatToolCall tc in completion.ToolCalls)
{
    try
    {
        await inspector.EnforceAsync(tc.FunctionName, tc.Id, tc.FunctionArguments.ToString());
    }
    catch (ClavenarDeniedException d)
    {
        Console.WriteLine($"blocked {d.ToolName}: {string.Join(", ", d.Reasons)}");
    }
}

Console.WriteLine($"inspected {completion.ToolCalls.Count} tool call(s)");
