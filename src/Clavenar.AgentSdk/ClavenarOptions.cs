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
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    public required string Endpoint { get; init; }

    public string? Token { get; init; }

    public Mode Mode { get; init; } = Mode.Enforce;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public RetryOptions Retry { get; init; } = RetryOptions.Defaults();

    /// <summary>An HTTP client to use; a shared default is used when null.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Reusable cached secure transport profile with explicit credential reload.</summary>
    public SecureTransportProfile? SecureTransport { get; init; }

    /// <summary>
    /// Permit credentials over HTTP only for an explicit 127.0.0.1 / ::1 DEV endpoint.
    /// </summary>
    public bool AllowInsecureLoopback { get; init; }

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

    internal (HttpClient Client, bool Owned) AcquireClient() =>
        SecureTransport is null ? (EffectiveClient, false) : (SecureTransport.Client(), false);

    internal string? EffectiveToken => SecureTransport?.Token() ?? Token;

    internal TimeSpan EffectiveTimeout => SecureTransport?.RequestTimeout ?? Timeout;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Endpoint))
        {
            throw new ClavenarConfigException("clavenar: Endpoint is required");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ClavenarConfigException(
                $"clavenar: Endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment: {Endpoint}");
        }

        bool hasCredentials = Token is not null || SecureTransport is not null;
        bool exactLoopback = endpoint.Host == "127.0.0.1" || endpoint.Host is "::1" or "[::1]";
        if (hasCredentials
            && endpoint.Scheme != Uri.UriSchemeHttps
            && (!AllowInsecureLoopback || !exactLoopback))
        {
            throw new ClavenarConfigException(
                "clavenar: credentials require HTTPS; insecure transport is allowed only for an explicit loopback DEV endpoint");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > MaxTimeout)
        {
            throw new ClavenarConfigException("clavenar: Timeout must be in (0, 5 minutes]");
        }

        if (Token is not null
            && (string.IsNullOrWhiteSpace(Token) || Token.Contains('\r') || Token.Contains('\n')))
        {
            throw new ClavenarConfigException("clavenar: Token must be non-empty and single-line");
        }

        if (Retry is null || Retry.MaxAttempts < 1 || Retry.MaxAttempts > 10)
        {
            throw new ClavenarConfigException("clavenar: Retry.MaxAttempts must be in [1, 10]");
        }

        if (Retry.BaseDelay < TimeSpan.Zero || Retry.BaseDelay > MaxRetryDelay)
        {
            throw new ClavenarConfigException("clavenar: Retry.BaseDelay must be in [0, 1 minute]");
        }

        if (SecureTransport is not null && (HttpClient is not null || Token is not null))
        {
            throw new ClavenarConfigException(
                "clavenar: SecureTransport cannot be combined with Token or HttpClient");
        }

        SecureTransport?.Validate();
    }
}
