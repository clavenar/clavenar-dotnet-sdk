namespace Clavenar.AgentSdk;

/// <summary>
/// A durable intent exists but its provider effect cannot yet be conclusively reconciled.
/// </summary>
public sealed class ClavenarRecoveryRequiredException : ClavenarException
{
    public ClavenarRecoveryRequiredException(string idempotencyId)
        : base($"clavenar execution {idempotencyId} requires provider reconciliation")
    {
        IdempotencyId = idempotencyId;
    }

    public string IdempotencyId { get; }
}
