namespace Clavenar.AgentSdk;

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Helpers for gating OpenAI Realtime function calls from a websocket message pump.</summary>
public static class Realtime
{
    /// <summary>
    /// The terminal event for one Realtime tool call: the <c>call_id</c> + final JSON-encoded
    /// <c>arguments</c> from <c>response.function_call_arguments.done</c>, plus the tool <c>name</c>.
    /// </summary>
    public sealed record FunctionCallDone(string CallId, string Name, string Arguments);

    /// <summary>
    /// Normalize a done event into a <see cref="NormalizedToolCall"/>. Arguments that don't parse are
    /// forwarded as a raw JSON string so a malformed-args policy rule can still inspect the attempt.
    /// </summary>
    public static NormalizedToolCall Normalize(FunctionCallDone evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        JsonNode? input;
        try
        {
            input = JsonNode.Parse(evt.Arguments);
        }
        catch (JsonException)
        {
            input = JsonValue.Create(evt.Arguments);
        }

        return new NormalizedToolCall(evt.CallId, evt.Name, input);
    }

    /// <summary>One-shot inspect for a Realtime function call. Returns the verdict; never throws on a deny.</summary>
    public static Task<Verdict> InspectAsync(
        FunctionCallDone evt, ClavenarOptions options, CancellationToken cancellationToken = default) =>
        new ClavenarInspector(options).InspectAsync(Normalize(evt), cancellationToken);
}
