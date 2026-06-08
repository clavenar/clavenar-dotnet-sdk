namespace Clavenar.AgentSdk;

using System.Threading;
using System.Threading.Tasks;

/// <summary>Convenience entry points over <see cref="ClavenarInspector"/>.</summary>
public static class Clavenar
{
    /// <summary>
    /// Inspect the tool calls in a provider response (an Anthropic message or an OpenAI chat
    /// completion), duck-typed via JSON. In enforce mode a denied / parked call throws.
    /// </summary>
    public static Task InspectResponseAsync(
        object response, ClavenarOptions options, CancellationToken cancellationToken = default) =>
        new ClavenarInspector(options).InspectResponseAsync(response, cancellationToken);
}
