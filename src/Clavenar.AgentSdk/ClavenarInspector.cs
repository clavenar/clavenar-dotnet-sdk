namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
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
    /// Inspect a complete sibling set through one ordered atomic decision and, in enforce mode,
    /// throw the first
    /// <see cref="ClavenarDeniedException"/> / <see cref="ClavenarPendingException"/> /
    /// <see cref="ClavenarRateLimitedException"/> in submission order. Observe mode never blocks.
    /// </summary>
    public async Task InspectAllAsync(
        IReadOnlyList<NormalizedToolCall> calls, CancellationToken cancellationToken = default)
    {
        if (calls is null || calls.Count == 0)
        {
            return;
        }

        bool enforce = _opts.Mode == Mode.Enforce;
        Verdict verdict;
        try
        {
            verdict = calls.Count == 1
                ? await Transport.InspectAsync(calls[0], _opts, cancellationToken).ConfigureAwait(false)
                : await Transport.InspectBatchAsync(calls, _opts, cancellationToken).ConfigureAwait(false);
        }
        catch (ClavenarTransportException error)
        {
            if (enforce)
            {
                throw;
            }

            foreach (var call in calls)
            {
                if (_opts.OnPolicyError is not null)
                {
                    await _opts.OnPolicyError(
                        error,
                        new VerdictContext(call.Name, call.Id, call.Input),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var ctx = new VerdictContext(call.Name, call.Id, call.Input);
            if (_opts.OnVerdict is not null)
            {
                await _opts.OnVerdict(verdict, ctx, cancellationToken).ConfigureAwait(false);
            }

            if (!enforce)
            {
                continue;
            }

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
                case VerdictKind.RateLimited:
                    throw new ClavenarRateLimitedException(
                        call.Name, verdict.RateLimitCode!, verdict.Reasons, verdict.RetryAfterSecs,
                        verdict.Layer, verdict.CorrelationId);
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
    public async Task InspectResponseAsync(object response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        JsonNode? tree;
        IReadOnlyList<NormalizedToolCall> calls;
        try
        {
            tree = JsonSerializer.SerializeToNode(response);
            calls = ExtractCalls(tree);
            if (calls.Count == 0 && DeclaresToolUse(tree))
            {
                throw new ClavenarTransportException(
                    "clavenar: provider response declared tool use but contained no valid tool call");
            }
        }
        catch (Exception error) when (
            error is ClavenarTransportException
                or ClavenarConfigException
                or JsonException
                or NotSupportedException)
        {
            var shapeError = error as ClavenarTransportException
                ?? new ClavenarTransportException(
                    "clavenar: provider response contained a malformed tool call", error);
            if (_opts.Mode == Mode.Enforce)
            {
                throw shapeError;
            }

            if (_opts.OnPolicyError is not null)
            {
                await _opts.OnPolicyError(
                    shapeError,
                    new VerdictContext("<provider-response>", "<unknown>", null),
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await InspectAllAsync(calls, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<NormalizedToolCall> ExtractCalls(JsonNode? tree)
    {
        var calls = new List<NormalizedToolCall>();
        if (tree is not JsonObject root)
        {
            throw new ClavenarTransportException(
                "clavenar: provider response must be a JSON object");
        }

        if (root["content"] is JsonArray content)
        {
            foreach (var b in content)
            {
                if (b is JsonObject bo && Str(bo["type"]) == "tool_use")
                {
                    if (string.IsNullOrEmpty(Str(bo["id"]))
                        || string.IsNullOrEmpty(Str(bo["name"])))
                    {
                        throw new ClavenarTransportException(
                            "clavenar: Anthropic tool_use block is missing a valid id or name");
                    }

                    calls.Add(new NormalizedToolCall(
                        Str(bo["id"]) ?? string.Empty, Str(bo["name"]) ?? string.Empty, bo["input"]?.DeepClone()));
                }
            }
        }
        else if (root["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                if (choice is not JsonObject choiceObject
                    || choiceObject["message"] is not JsonObject message)
                {
                    throw new ClavenarTransportException(
                        "clavenar: OpenAI choice is missing a valid message object");
                }

                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        if (tc is not JsonObject tco || Str(tco["type"]) != "function")
                        {
                            throw new ClavenarTransportException(
                                "clavenar: OpenAI tool_call has an unsupported or missing type");
                        }

                        var fn = tco["function"];
                        if (string.IsNullOrEmpty(Str(tco["id"]))
                            || string.IsNullOrEmpty(Str(fn?["name"]))
                            || Str(fn?["arguments"]) is null)
                        {
                            throw new ClavenarTransportException(
                                "clavenar: OpenAI tool_call is missing a valid id, name, or arguments string");
                        }

                        calls.Add(NormalizedToolCall.FromJsonArguments(
                            Str(tco["id"])!, Str(fn?["name"])!, Str(fn?["arguments"])!));
                    }
                }
            }
        }
        else
        {
            throw new ClavenarTransportException(
                "clavenar: provider response is missing a content or choices array");
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

    internal async Task ProviderShapeErrorAsync(
        string message, CancellationToken cancellationToken = default)
    {
        var error = new ClavenarTransportException(message);
        if (_opts.Mode == Mode.Enforce)
        {
            throw error;
        }

        if (_opts.OnPolicyError is not null)
        {
            await _opts.OnPolicyError(
                error,
                new VerdictContext("<provider-stream>", "<unknown>", null),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
