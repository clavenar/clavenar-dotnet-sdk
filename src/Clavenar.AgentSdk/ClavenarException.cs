namespace Clavenar.AgentSdk;

using System;

/// <summary>
/// Root of the SDK's exceptions, so callers can <c>catch (ClavenarException)</c> at a tool boundary.
/// </summary>
public abstract class ClavenarException : Exception
{
    private protected ClavenarException(string message)
        : base(message) { }
}
