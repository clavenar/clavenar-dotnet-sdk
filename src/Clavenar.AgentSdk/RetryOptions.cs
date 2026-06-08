namespace Clavenar.AgentSdk;

using System;

/// <summary>
/// Per-inspection retry policy. Network errors and 5xx responses retry up to
/// <see cref="MaxAttempts"/> with full-jitter exponential backoff (<see cref="BaseDelay"/> *
/// 2^attempt); 200 / 403 / other-4xx never retry.
/// </summary>
public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay)
{
    public static RetryOptions Defaults() => new(3, TimeSpan.FromMilliseconds(100));
}
