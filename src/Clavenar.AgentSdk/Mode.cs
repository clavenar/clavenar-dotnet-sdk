namespace Clavenar.AgentSdk;

/// <summary>Whether a deny / pending verdict blocks the agent.</summary>
public enum Mode
{
    /// <summary>Deny / pending throw; a transport failure fails closed.</summary>
    Enforce,

    /// <summary>Nothing blocks; verdicts surface via callbacks and the call passes through.</summary>
    Observe,
}
