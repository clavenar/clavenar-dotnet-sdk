namespace Clavenar.AgentSdk;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Developer-mode rendering of a denied tool call's verbose-verdict breakdown. When
/// <see cref="ClavenarOptions.DevMode"/> is set, the SDK writes a readable panel (per-detector
/// scores, degraded lanes, reasons, correlation id) to stderr before throwing
/// <see cref="ClavenarDeniedException"/>.
/// </summary>
public static class DevMode
{
    /// <summary>
    /// Render a denied tool call as a readable console panel. Pure (no I/O) so it's unit-testable;
    /// falls back to a hint when the gateway didn't include detail (verbose-verdicts off).
    /// </summary>
    public static string RenderDenyPanel(ClavenarDeniedException d)
    {
        var b = new StringBuilder();
        b.Append("━━ clavenar denied: ").Append(d.ToolName).Append(" ━━");

        var meta = new StringBuilder();
        if (!string.IsNullOrEmpty(d.Layer))
        {
            meta.Append("layer=").Append(d.Layer);
        }

        if (!string.IsNullOrEmpty(d.IntentCategory))
        {
            if (meta.Length > 0)
            {
                meta.Append("  ");
            }

            meta.Append("intent=").Append(d.IntentCategory);
        }

        if (!string.IsNullOrEmpty(d.CorrelationId))
        {
            if (meta.Length > 0)
            {
                meta.Append("  ");
            }

            meta.Append("correlation=").Append(d.CorrelationId);
        }

        if (meta.Length > 0)
        {
            b.Append("\n  ").Append(meta);
        }

        if (d.Reasons.Count > 0)
        {
            b.Append("\n  reasons:");
            foreach (var r in d.Reasons)
            {
                b.Append("\n    - ").Append(r);
            }
        }

        if (d.Detail is { Detectors.Count: > 0 } detail)
        {
            b.Append("\n  detectors:");
            foreach (var det in detail.Detectors)
            {
                b.Append("\n    ")
                    .Append(det.Detector.PadRight(22))
                    .Append(det.Score.ToString("0.00", CultureInfo.InvariantCulture));
                if (det.Flagged)
                {
                    b.Append("  ⚠ flagged");
                }
            }

            if (detail.Degraded.Count > 0)
            {
                b.Append("\n  degraded: ").Append(string.Join(", ", detail.Degraded));
            }
        }
        else
        {
            b.Append("\n  (no per-detector detail — run the gateway with CLAVENAR_PROXY_VERBOSE_VERDICTS=true)");
        }

        if (!string.IsNullOrEmpty(d.CorrelationId))
        {
            b.Append("\n  trace: look up correlation ").Append(d.CorrelationId).Append(" in the console audit trail");
        }

        return b.ToString();
    }

    /// <summary>Write the dev-mode deny panel to stderr. Best-effort.</summary>
    internal static void EmitDenyPanel(ClavenarDeniedException d)
    {
        Console.Error.WriteLine(RenderDenyPanel(d));
    }
}
