namespace Clavenar.AgentSdk;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Configuration for inspection. <see cref="Endpoint"/> is required; the rest default to enforce
/// mode, a 10s per-request timeout, and 3 retries at a 100ms base delay.
/// </summary>
public sealed record ClavenarOptions
{
    private static readonly HttpClient SharedClient = new();

    public required string Endpoint { get; init; }

    public string? Token { get; init; }

    public Mode Mode { get; init; } = Mode.Enforce;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public RetryOptions Retry { get; init; } = RetryOptions.Defaults();

    /// <summary>An HTTP client to use; a shared default is used when null.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Fires per inspected call before any deny→throw translation, in both modes.</summary>
    public Func<Verdict, VerdictContext, CancellationToken, Task>? OnVerdict { get; init; }

    /// <summary>Fires (observe mode only) when an inspection fails at the transport layer.</summary>
    public Func<ClavenarTransportException, VerdictContext, CancellationToken, Task>? OnPolicyError { get; init; }

    /// <summary>
    /// Developer mode: render the gateway's verbose-verdict detail to stderr on a denied call before
    /// throwing. Off by default. Dev/staging only — detailed denials are an attacker oracle.
    /// </summary>
    public bool DevMode { get; init; }

    internal HttpClient EffectiveClient => HttpClient ?? SharedClient;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            throw new ClavenarConfigException("clavenar: Endpoint is required");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new ClavenarConfigException($"clavenar: Endpoint is not a valid absolute URL: {Endpoint}");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ClavenarConfigException("clavenar: Timeout must be positive");
        }
    }
}
