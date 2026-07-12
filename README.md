# clavenar-dotnet-sdk

[![CI](https://github.com/clavenar/clavenar-dotnet-sdk/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/clavenar/clavenar-dotnet-sdk/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Clavenar.AgentSdk.svg)](https://www.nuget.org/packages/Clavenar.AgentSdk)

.NET SDK for [Clavenar](https://clavenar.com). Inspect the tool calls a
model emits against your policies *before* your agent runs them.

Part of the by-language agent-wrapper SDK family alongside
[`@clavenar/agent-sdk`](https://github.com/clavenar/clavenar-typescript-sdk)
(TypeScript) and
[`clavenar-agent-sdk`](https://github.com/clavenar/clavenar-python-sdk)
(Python) — all speak the same wire contract.

## Install

```bash
dotnet add package Clavenar.AgentSdk
```

Targets `net8.0`. The only dependency is the in-box `System.Text.Json`;
the SDK takes **no dependency on the OpenAI or Anthropic SDKs** — it
duck-types their responses.

## Two ways to integrate

### 1. Inspect at the tool-dispatch boundary (recommended)

Semantic Kernel and most agent frameworks own the model call, so gate the
tool *before* it executes — e.g. from an `IFunctionInvocationFilter`:

```csharp
var inspector = new ClavenarInspector(new ClavenarOptions
{
    Endpoint = "http://localhost:8088",
    Token = token,
});

// inside your filter / tool dispatcher:
await inspector.EnforceAsync(toolName, toolCallId, argumentsJson, ct);
// throws ClavenarDeniedException on a policy block; reached only when cleared
```

### 2. Inspect a provider response (wrap-and-forget)

After a raw provider call, hand the response to clavenar — it duck-types
an Anthropic message or an OpenAI chat completion:

```csharp
ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options);
await Clavenar.InspectResponseAsync(completion, opts); // throws on a denied tool call
// only policy-cleared tool calls remain — run them
```

## Verdicts and the error model

`ClavenarInspector.InspectAsync` returns a `Verdict`
(`Allow` / `Deny` / `Pending` / `RateLimited`). The batch / enforce
paths translate, in enforce mode, to exceptions rooted at
`ClavenarException`:

| Exception | Meaning |
|---|---|
| `ClavenarDeniedException` | policy rejected the call — `ToolName`, `Reasons`, `ReviewReasons`, `IntentCategory`, `Layer`, `CorrelationId` |
| `ClavenarPendingException` | parked for human review — `await ResolveAsync()` to block until decided |
| `ClavenarRateLimitedException` | gateway rejected the call before evaluation — `Code` (`rate_limited` velocity gate / `quota_exceeded` spend gate), `RetryAfterSecs` (null on `quota_exceeded`) |
| `ClavenarTransportException` | clavenar unreachable / unexpected response — `Status` (0 = network) |
| `ClavenarConfigException` | bad options, or a model tool call with unparseable arguments |

## Debugging a denial

`ClavenarDeniedException` carries `Reasons`, `Layer`, and `CorrelationId`.
To see *which detector* fired, run the gateway with
`CLAVENAR_PROXY_VERBOSE_VERDICTS=true` (Lite: `--verbose-verdicts`) — the
deny then carries a per-detector `Detail` breakdown, and the SDK renders
it to stderr when you set `DevMode = true`:

```csharp
var opts = new ClavenarOptions
{
    Endpoint = "https://clavenar.internal",
    DevMode = true, // dev/staging only — detailed denials are an attacker oracle
};
// On a deny, the SDK prints a panel to stderr:
//   ━━ clavenar denied: send_email ━━
//     layer=brain  intent=Exfiltration  correlation=abc-123
//     detectors:
//       persona_drift         0.12
//       injection             0.91  ⚠ flagged
//     degraded: injection
```

Programmatic access (no `DevMode` needed):

```csharp
catch (ClavenarDeniedException e)
{
    if (e.Detail is not null)
    {
        foreach (var d in e.Detail.Detectors)
            if (d.Flagged || d.Score >= 0.5)
                Console.WriteLine($"fired: {d.Detector} ({d.Score:0.00})");
    }
}
```

`Detail` is null unless the gateway opts in; without it the panel prints a
hint. `DevMode.RenderDenyPanel(e)` returns the string directly.

## Enforce vs observe

```csharp
var opts = new ClavenarOptions
{
    Endpoint = endpoint,
    Mode = Mode.Observe,
    OnVerdict = (verdict, ctx, ct) =>
    {
        logger.LogInformation("{Tool} -> {Kind}", ctx.ToolName, verdict.Kind);
        return Task.CompletedTask;
    },
};
```

Observe never blocks: verdicts surface via `OnVerdict`, transport failures
via `OnPolicyError`, and every call passes through — the rollout knob for
tuning policies against live traffic.

## Pending review

```csharp
try
{
    await inspector.EnforceAsync(toolName, id, argsJson, ct);
}
catch (ClavenarPendingException pending)
{
    await pending.ResolveAsync(); // returns on approve, throws ClavenarDeniedException on deny
}
```

## Streaming

`StreamGate` holds a tool call's closing event until clavenar returns a
verdict, so a denied call never reaches your loop as actionable. Drive it
from your streaming loop with `Start` / `Update` / `CloseAsync` (per tool)
or `CloseByPrefixAsync` (OpenAI per-choice drain). See
[`docs/SEQUENCES.md`](docs/SEQUENCES.md).

## Realtime

```csharp
Verdict v = await Realtime.InspectAsync(
    new Realtime.FunctionCallDone(callId, name, argumentsJson), opts);
```

## Behavior parity

Matches the TypeScript reference 1:1 on the wire — see
[`docs/PARITY.md`](docs/PARITY.md) for the map and the additive .NET-idiom
differences.

## License

[Apache-2.0](LICENSE).
