# clavenar-dotnet-sdk

[![CI](https://github.com/clavenar/clavenar-dotnet-sdk/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/clavenar/clavenar-dotnet-sdk/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/clavenar/clavenar-dotnet-sdk)](https://github.com/clavenar/clavenar-dotnet-sdk/releases)

.NET SDK for [Clavenar](https://clavenar.com). Inspect the tool calls a
model emits against your policies *before* your agent runs them.

Part of the by-language agent-wrapper SDK family alongside
[`@clavenar/agent-sdk`](https://github.com/clavenar/clavenar-typescript-sdk)
(TypeScript) and
[`clavenar-agent-sdk`](https://github.com/clavenar/clavenar-python-sdk)
(Python) — all speak the same wire contract.

## Install

```bash
dotnet nuget add source https://nuget.pkg.github.com/clavenar/index.json \
  --name clavenar --username YOUR_GITHUB_USER --password YOUR_GITHUB_TOKEN \
  --store-password-in-clear-text
dotnet add package Clavenar.AgentSdk --version 1.6.5 --source clavenar
```

The token needs `read:packages`. The exact `.nupkg` and symbols package are
also attached anonymously to the versioned GitHub release.

```bash
base=https://github.com/clavenar/clavenar-dotnet-sdk/releases/download/v1.6.5
curl -fsSLO "$base/Clavenar.AgentSdk.1.6.5.nupkg"
curl -fsSLO "$base/Clavenar.AgentSdk.1.6.5.snupkg"
unzip -t Clavenar.AgentSdk.1.6.5.nupkg
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
paths explicitly select side-effect-free `clavenar.decision/v1`; the UUID is
allocated before the first attempt and a multi-tool turn uses one ordered
atomic decision. Proxy 1.0.0 and Lite 1.0.0 reject unselected tool calls with
HTTP 426; upgrade this SDK before the gateway by following
<https://clavenar.com/docs/sdk-migration/>. They translate, in enforce mode, to exceptions rooted at
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

Pending polling treats only network failures and 5xx responses as transient.
Malformed success bodies, correlation mismatches, and every other HTTP status
are terminal transport errors.

## Governed execution

Use `GovernedExecutionClient` when policy authorization and the provider effect
must form a recoverable workflow. Supply an application-owned
`IDurableExecutionStore`, a cryptographic `IAuthorizationVerifier`, a receipt
signer, and an executor that forwards the supplied idempotency ID to the
provider. The client verifies all authorization bindings before committing an
intent or releasing an effect.

On restart, a stored completion is integrity-checked and returned. A stored
intent is passed to the optional `IEffectRecoverer`; if it cannot conclusively
find the provider effect, the client throws
`ClavenarRecoveryRequiredException` instead of replaying it. Completion plus
receipt-outbox persistence is bounded by the configured finalization deadline.

`SecureTransportProfile` reuses its connection pool. Call `Reload()` after
rotating credential files and dispose the profile during application shutdown.

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
