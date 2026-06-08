namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thrown (enforce mode) when clavenar parks a tool call for human review. Call
/// <see cref="ResolveAsync(CancellationToken)"/> to block until an operator decides: it returns on
/// approve and throws <see cref="ClavenarDeniedException"/> on deny.
/// </summary>
public sealed class ClavenarPendingException : ClavenarException
{
    private readonly Func<CancellationToken, Task<ClavenarPendingView>> _pollOnce;

    internal ClavenarPendingException(
        string toolName,
        string correlationId,
        IReadOnlyList<string> reviewReasons,
        Func<CancellationToken, Task<ClavenarPendingView>> pollOnce)
        : base($"clavenar parked tool \"{toolName}\" for review (correlation_id={correlationId})")
    {
        ToolName = toolName;
        CorrelationId = correlationId;
        ReviewReasons = reviewReasons;
        _pollOnce = pollOnce;
    }

    public string ToolName { get; }

    public string CorrelationId { get; }

    public IReadOnlyList<string> ReviewReasons { get; }

    /// <summary>Block until decided, using the default 2s poll interval and 10-minute ceiling.</summary>
    public Task ResolveAsync(CancellationToken cancellationToken = default) =>
        ResolveAsync(null, cancellationToken);

    /// <summary>
    /// Block until an operator decides. Polls <c>GET /pending/{id}</c> every poll interval and
    /// returns on approve or throws <see cref="ClavenarDeniedException"/> on deny. Transient
    /// transport failures (5xx, network) are swallowed; 401 / 404 are terminal. A blown deadline
    /// throws <see cref="ClavenarTransportException"/>.
    /// </summary>
    public async Task ResolveAsync(ResolveOptions? options, CancellationToken cancellationToken = default)
    {
        var o = options ?? ResolveOptions.Defaults();
        if (o.PollInterval <= TimeSpan.Zero)
        {
            throw new ClavenarTransportException("ResolveOptions.PollInterval must be positive");
        }

        if (o.Timeout <= TimeSpan.Zero)
        {
            throw new ClavenarTransportException("ResolveOptions.Timeout must be positive");
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < o.Timeout)
        {
            ClavenarPendingView? view = null;
            try
            {
                view = await _pollOnce(cancellationToken).ConfigureAwait(false);
            }
            catch (ClavenarTransportException e) when (e.Status != 401 && e.Status != 404)
            {
                // 5xx / network: swallow and retry next tick. 401 / 404 propagate.
            }

            if (view?.Decision is { } decision)
            {
                if (decision == "allow")
                {
                    return;
                }

                if (decision == "deny")
                {
                    var reasons =
                        !string.IsNullOrEmpty(view.DeciderNote)
                            ? new List<string> { view.DeciderNote! }
                            : new List<string> { "operator denied" };
                    throw new ClavenarDeniedException(
                        ToolName, reasons, ReviewReasons, "PendingDenied", null, CorrelationId);
                }
            }

            var remaining = o.Timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < o.PollInterval ? remaining : o.PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new ClavenarTransportException(
            $"clavenar pending {CorrelationId} not decided within {o.Timeout.TotalMilliseconds}ms");
    }
}
