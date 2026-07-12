namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The primary inspection surface for framework integrations (Semantic Kernel function filters,
/// custom tool dispatchers): build <see cref="NormalizedToolCall"/>s at your tool boundary and
/// inspect them before running the tools.
/// </summary>
public sealed class ClavenarInspector
{
    private readonly ClavenarOptions _opts;

    public ClavenarInspector(ClavenarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _opts = options;
    }

    /// <summary>Inspect one tool call and return its verdict. Never throws on a deny.</summary>
    public Task<Verdict> InspectAsync(NormalizedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Transport.InspectAsync(call, _opts, cancellationToken);
    }

    /// <summary>A single <c>GET /pending/{id}</c> poll.</summary>
    public Task<ClavenarPendingView> PollPendingOnceAsync(
        string correlationId, CancellationToken cancellationToken = default) =>
        Transport.PollPendingOnceAsync(correlationId, _opts, cancellationToken);

    /// <summary>
    /// Inspect a batch concurrently and, in enforce mode, throw the first
    /// <see cref="ClavenarDeniedException"/> / <see cref="ClavenarPendingException"/> in submission
    /// order. Observe mode never blocks; a per-call transport failure fires OnPolicyError.
    /// </summary>
    public async Task InspectAllAsync(
        IReadOnlyList<NormalizedToolCall> calls, CancellationToken cancellationToken = default)
    {
        if (calls is null || calls.Count == 0)
        {
            return;
        }

        bool enforce = _opts.Mode == Mode.Enforce;
        var outcomes = await Task.WhenAll(calls.Select(c => InspectOutcomeAsync(c, cancellationToken)))
            .ConfigureAwait(false);

        // Fail closed before processing any deny, matching Promise.all semantics.
        if (enforce)
        {
            foreach (var o in outcomes)
            {
                if (o.Error is not null)
                {
                    throw o.Error;
                }
            }
        }

        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var outcome = outcomes[i];
            var ctx = new VerdictContext(call.Name, call.Id, call.Input);
            if (outcome.Error is not null)
            {
                if (_opts.OnPolicyError is not null)
                {
                    await _opts.OnPolicyError(outcome.Error, ctx, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (_opts.OnVerdict is not null)
            {
                await _opts.OnVerdict(outcome.Verdict!, ctx, cancellationToken).ConfigureAwait(false);
            }

            if (!enforce)
            {
                continue;
            }

            var verdict = outcome.Verdict!;
            switch (verdict.Kind)
            {
                case VerdictKind.Deny:
                    var denied = new ClavenarDeniedException(
                        call.Name, verdict.Reasons, verdict.ReviewReasons, verdict.IntentCategory,
                        verdict.Layer, verdict.CorrelationId, verdict.Detail);
                    if (_opts.DevMode)
                    {
                        DevMode.EmitDenyPanel(denied);
                    }

                    throw denied;
                case VerdictKind.Pending:
                    var corr = verdict.CorrelationId!;
                    throw new ClavenarPendingException(
                        call.Name, corr, verdict.ReviewReasons,
                        c => Transport.PollPendingOnceAsync(corr, _opts, c));
                default:
                    break;
            }
        }
    }

    /// <summary>Inspect one tool call whose arguments are already a JSON node.</summary>
    public Task EnforceAsync(
        string toolName, string toolCallId, JsonNode? arguments, CancellationToken cancellationToken = default) =>
        InspectAllAsync(new[] { new NormalizedToolCall(toolCallId, toolName, arguments) }, cancellationToken);

    /// <summary>Inspect one tool call whose arguments are a JSON-encoded string.</summary>
    public Task EnforceAsync(
        string toolName, string toolCallId, string argumentsJson, CancellationToken cancellationToken = default) =>
        InspectAllAsync(
            new[] { NormalizedToolCall.FromJsonArguments(toolCallId, toolName, argumentsJson) }, cancellationToken);

    /// <summary>
    /// Inspect the tool calls in a provider response — an Anthropic message (a <c>content</c> array)
    /// or an OpenAI chat completion (a <c>choices</c> array) — duck-typed via JSON, so no provider
    /// SDK dependency is required.
    /// </summary>
    public Task InspectResponseAsync(object response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var tree = JsonSerializer.SerializeToNode(response);
        var calls = ExtractCalls(tree);
        if (calls.Count == 0 && DeclaresToolUse(tree))
        {
            Trace.TraceWarning(
                "clavenar: response declares tool use (stop_reason/finish_reason) but no tool calls"
                + " were extracted — the provider response shape may have drifted; tool calls were"
                + " NOT inspected");
        }

        return InspectAllAsync(calls, cancellationToken);
    }

    private async Task<Outcome> InspectOutcomeAsync(NormalizedToolCall call, CancellationToken ct)
    {
        try
        {
            return Outcome.Ok(await Transport.InspectAsync(call, _opts, ct).ConfigureAwait(false));
        }
        catch (ClavenarTransportException e)
        {
            return Outcome.Fail(e);
        }
    }

    private static IReadOnlyList<NormalizedToolCall> ExtractCalls(JsonNode? tree)
    {
        var calls = new List<NormalizedToolCall>();
        if (tree is null)
        {
            return calls;
        }

        if (tree["content"] is JsonArray content)
        {
            foreach (var b in content)
            {
                if (b is JsonObject bo && Str(bo["type"]) == "tool_use")
                {
                    calls.Add(new NormalizedToolCall(
                        Str(bo["id"]) ?? string.Empty, Str(bo["name"]) ?? string.Empty, bo["input"]?.DeepClone()));
                }
            }
        }
        else if (tree["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                if (choice?["message"]?["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        if (tc is JsonObject tco && Str(tco["type"]) == "function")
                        {
                            var fn = tco["function"];
                            calls.Add(NormalizedToolCall.FromJsonArguments(
                                Str(tco["id"]) ?? string.Empty, Str(fn?["name"]) ?? string.Empty,
                                Str(fn?["arguments"]) ?? string.Empty));
                        }
                    }
                }
            }
        }

        return calls;
    }

    /// <summary>True when the provider marked the turn as tool-calling, whatever the content shape.</summary>
    internal static bool DeclaresToolUse(JsonNode? tree)
    {
        if (tree is null)
        {
            return false;
        }

        if (Str(tree["stop_reason"]) == "tool_use")
        {
            return true;
        }

        if (tree["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                if (choice is JsonObject co && Str(co["finish_reason"]) == "tool_calls")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private readonly struct Outcome
    {
        private Outcome(Verdict? verdict, ClavenarTransportException? error)
        {
            Verdict = verdict;
            Error = error;
        }

        public Verdict? Verdict { get; }

        public ClavenarTransportException? Error { get; }

        public static Outcome Ok(Verdict v) => new(v, null);

        public static Outcome Fail(ClavenarTransportException e) => new(null, e);
    }
}
