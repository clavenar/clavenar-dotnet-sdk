namespace Clavenar.AgentSdk;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>Body of <c>GET /pending/{id}</c>. <see cref="Decision"/> is null until an operator decides.</summary>
public sealed class ClavenarPendingView
{
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("tool_type")]
    public string? ToolType { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("review_reasons")]
    public IReadOnlyList<string>? ReviewReasons { get; init; }

    [JsonPropertyName("requested_at")]
    public string? RequestedAt { get; init; }

    [JsonPropertyName("decided_at")]
    public string? DecidedAt { get; init; }

    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    [JsonPropertyName("decider_note")]
    public string? DeciderNote { get; init; }
}
