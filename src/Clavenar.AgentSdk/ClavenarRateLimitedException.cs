namespace Clavenar.AgentSdk;

using System.Collections.Generic;

/// <summary>
/// Thrown (enforce mode) when clavenar returns HTTP 429 for a tool call — rejected before
/// evaluation, by the request-velocity gate (<c>rate_limited</c>) or the per-tenant spend gate
/// (<c>quota_exceeded</c>). Never retried by the transport: honor <see cref="RetryAfterSecs"/>
/// (set on <c>rate_limited</c> only) or fail the operation.
/// </summary>
public sealed class ClavenarRateLimitedException : ClavenarException
{
    internal ClavenarRateLimitedException(
        string toolName,
        string code,
        IReadOnlyList<string> reasons,
        int? retryAfterSecs,
        string? layer,
        string? correlationId)
        : base(
            $"clavenar {code} for tool \"{toolName}\""
            + (retryAfterSecs is null ? string.Empty : $" (retry after {retryAfterSecs}s)"))
    {
        ToolName = toolName;
        Code = code;
        Reasons = reasons;
        RetryAfterSecs = retryAfterSecs;
        Layer = layer;
        CorrelationId = correlationId;
    }

    /// <summary>The tool call that was rate-limited.</summary>
    public string ToolName { get; }

    /// <summary>Which gate fired: <c>"rate_limited"</c> or <c>"quota_exceeded"</c>.</summary>
    public string Code { get; }

    /// <summary>Human-readable reasons reported by the gateway; empty when it sent none.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>Seconds to wait before retrying; null when absent (always on <c>quota_exceeded</c>).</summary>
    public int? RetryAfterSecs { get; }

    /// <summary>The stage that produced the verdict when reported, else null.</summary>
    public string? Layer { get; }

    /// <summary>clavenar's correlation id for the audit ledger when reported, else null.</summary>
    public string? CorrelationId { get; }
}
