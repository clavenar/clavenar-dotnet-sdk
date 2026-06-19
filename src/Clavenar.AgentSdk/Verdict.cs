namespace Clavenar.AgentSdk;

using System.Collections.Generic;

/// <summary>
/// Result of inspecting one tool call. <see cref="ClavenarInspector.InspectAsync"/> returns it
/// directly; the batch / enforce paths turn Deny / Pending into thrown exceptions in enforce mode.
/// </summary>
public sealed class Verdict
{
    internal Verdict(
        VerdictKind kind,
        string? correlationId,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> reviewReasons,
        string intentCategory,
        string? layer,
        VerdictDetail? detail = null)
    {
        Kind = kind;
        CorrelationId = correlationId;
        Reasons = reasons;
        ReviewReasons = reviewReasons;
        IntentCategory = intentCategory;
        Layer = layer;
        Detail = detail;
    }

    public VerdictKind Kind { get; }

    /// <summary>clavenar's correlation id when the deployment sets the header, else null.</summary>
    public string? CorrelationId { get; }

    public IReadOnlyList<string> Reasons { get; }

    public IReadOnlyList<string> ReviewReasons { get; }

    public string IntentCategory { get; }

    /// <summary>The stage that produced a deny when reported, else null.</summary>
    public string? Layer { get; }

    /// <summary>The verbose-verdict per-detector breakdown when the gateway opts in, else null.</summary>
    public VerdictDetail? Detail { get; }
}
