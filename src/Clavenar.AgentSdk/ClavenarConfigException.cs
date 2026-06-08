namespace Clavenar.AgentSdk;

/// <summary>Thrown for malformed configuration, or a model tool call with unparseable arguments.</summary>
public sealed class ClavenarConfigException : ClavenarException
{
    public ClavenarConfigException(string message)
        : base(message) { }
}
