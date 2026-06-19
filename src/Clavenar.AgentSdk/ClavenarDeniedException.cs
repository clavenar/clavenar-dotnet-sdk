namespace Clavenar.AgentSdk;

using System.Collections.Generic;

/// <summary>Thrown (enforce mode) when clavenar rejects a tool call.</summary>
public sealed class ClavenarDeniedException : ClavenarException
{
    internal ClavenarDeniedException(
        string toolName,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> reviewReasons,
        string intentCategory,
        string? layer,
        string? correlationId,
        VerdictDetail? detail = null)
        : base($"clavenar denied tool \"{toolName}\": {string.Join(" | ", reasons)}")
    {
        ToolName = toolName;
        Reasons = reasons;
        ReviewReasons = reviewReasons;
        IntentCategory = intentCategory;
        Layer = layer;
        CorrelationId = correlationId;
        Detail = detail;
    }

    public string ToolName { get; }

    public IReadOnlyList<string> Reasons { get; }

    public IReadOnlyList<string> ReviewReasons { get; }

    public string IntentCategory { get; }

    /// <summary>The stage that produced the deny when reported, else null.</summary>
    public string? Layer { get; }

    /// <summary>clavenar's correlation id for the audit ledger when reported, else null.</summary>
    public string? CorrelationId { get; }

    /// <summary>The verbose-verdict per-detector breakdown when the gateway opts in, else null.</summary>
    public VerdictDetail? Detail { get; }
}
