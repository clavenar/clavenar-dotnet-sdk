namespace Clavenar.AgentSdk;

using System.Collections.Generic;

/// <summary>
/// One detector's contribution to a verbose-verdict deny. <see cref="Score"/> is the numeric signal
/// in [0,1]; <see cref="Flagged"/> is the boolean verdict on the boolean lanes (injection /
/// malicious_code / compromised_package) and false on the numeric lanes (persona_drift /
/// sequence_escalation), where <see cref="Score"/> is the value to read.
/// </summary>
public sealed record DetectorScore(string Detector, double Score, bool Flagged);

/// <summary>
/// The verbose-verdict per-detector breakdown attached to a deny when the gateway runs with
/// <c>CLAVENAR_PROXY_VERBOSE_VERDICTS=true</c>. Null on the deny otherwise.
/// </summary>
public sealed record VerdictDetail(
    IReadOnlyList<DetectorScore> Detectors,
    IReadOnlyList<string> Degraded);
