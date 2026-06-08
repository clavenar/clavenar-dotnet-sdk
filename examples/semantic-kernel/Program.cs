using Clavenar.AgentSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

var inspector = new ClavenarInspector(new ClavenarOptions
{
    Endpoint = Environment.GetEnvironmentVariable("CLAVENAR_ENDPOINT") ?? "http://localhost:8088",
    Token = Environment.GetEnvironmentVariable("CLAVENAR_LITE_TOKEN"),
});

var builder = Kernel.CreateBuilder();
builder.Services.AddSingleton<IFunctionInvocationFilter>(new ClavenarFilter(inspector));
Kernel kernel = builder.Build();

Console.WriteLine($"Kernel built with {kernel.GetType().Name}'s Clavenar filter — tool calls are gated before they run.");

// Gates each Semantic Kernel function/tool invocation: clavenar inspects the arguments before the
// function body runs, throwing ClavenarDeniedException on a policy block.
internal sealed class ClavenarFilter(ClavenarInspector inspector) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var argumentsJson = System.Text.Json.JsonSerializer.Serialize(context.Arguments);
        await inspector.EnforceAsync(context.Function.Name, context.Function.Name, argumentsJson);
        await next(context);
    }
}
