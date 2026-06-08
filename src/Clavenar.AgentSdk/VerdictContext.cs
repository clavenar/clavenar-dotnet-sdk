namespace Clavenar.AgentSdk;

using System.Text.Json.Nodes;

/// <summary>Identifies the tool call an OnVerdict / OnPolicyError callback fired for.</summary>
public sealed record VerdictContext(string ToolName, string ToolUseId, JsonNode? ToolInput);
