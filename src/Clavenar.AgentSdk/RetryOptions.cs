namespace Clavenar.AgentSdk;

using System;

/// <summary>
/// Retry policy for the explicitly selected, side-effect-free decision request only.
/// Network errors and 5xx responses retry up to <see cref="MaxAttempts"/> with the
/// original canonical idempotency ID and full-jitter exponential backoff
/// (<see cref="BaseDelay"/> * 2^attempt). Registered executors and other effect-capable
/// operations are outside this retry loop; 200 / 403 / other-4xx never retry.
/// </summary>
public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay)
{
    public static RetryOptions Defaults() => new(3, TimeSpan.FromMilliseconds(100));
}
