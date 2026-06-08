namespace Clavenar.AgentSdk;

using System;

/// <summary>Tunes <see cref="ClavenarPendingException"/>'s resolve loop. Defaults: 2s poll, 10-minute ceiling.</summary>
public sealed record ResolveOptions(TimeSpan PollInterval, TimeSpan Timeout)
{
    public static ResolveOptions Defaults() => new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10));
}
