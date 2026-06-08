using Clavenar.AgentSdk;

var opts = new ClavenarOptions
{
    Endpoint = Environment.GetEnvironmentVariable("CLAVENAR_ENDPOINT") ?? "http://localhost:8088",
};

// In a real pump you'd assemble this from response.output_item.added (call_id + name) and
// response.function_call_arguments.done (arguments).
var evt = new Realtime.FunctionCallDone("call_42", "wire_transfer", "{\"amount\":5000,\"to\":\"external\"}");

Verdict v = await Realtime.InspectAsync(evt, opts);
Console.WriteLine(v.Kind switch
{
    VerdictKind.Deny => $"deny {evt.Name}: {string.Join(", ", v.Reasons)}",
    VerdictKind.Pending => $"pending {evt.Name} ({v.CorrelationId})",
    _ => $"allow — dispatch {evt.Name}",
});
