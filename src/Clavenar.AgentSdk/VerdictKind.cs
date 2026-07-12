namespace Clavenar.AgentSdk;

/// <summary>Outcome of inspecting one tool call.</summary>
public enum VerdictKind
{
    Allow,
    Deny,
    Pending,
    RateLimited,
}
