namespace Clavenar.AgentSdk;

/// <summary>
/// Thrown when clavenar is unreachable or returns an unexpected response. <see cref="Status"/> is
/// the HTTP status when one was received, or 0 for a network-level failure (which is retriable).
/// </summary>
public sealed class ClavenarTransportException : ClavenarException
{
    public ClavenarTransportException(string message, int status = 0)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The HTTP status, or 0 when the request never received an HTTP response.</summary>
    public int Status { get; }
}
