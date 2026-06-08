namespace Clavenar.AgentSdk;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A provider-agnostic tool call ready for inspection. <see cref="Input"/> is the model's argument
/// payload, forwarded to clavenar verbatim.
/// </summary>
public sealed record NormalizedToolCall(string Id, string Name, JsonNode? Input)
{
    /// <summary>
    /// Build a tool call from a JSON-encoded arguments string. Throws
    /// <see cref="ClavenarConfigException"/> when the arguments aren't valid JSON (a model-contract
    /// violation). An empty string becomes an empty object.
    /// </summary>
    public static NormalizedToolCall FromJsonArguments(string id, string name, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
        {
            return new NormalizedToolCall(id, name, new JsonObject());
        }

        try
        {
            return new NormalizedToolCall(id, name, JsonNode.Parse(argumentsJson));
        }
        catch (JsonException e)
        {
            throw new ClavenarConfigException(
                $"tool call {id} ({name}) had unparseable arguments: {e.Message}");
        }
    }
}
