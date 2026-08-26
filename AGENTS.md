<!-- public repo — do not add internal topology, secrets, deploy/runbook, strategy, or absolute host paths -->
# clavenar-dotnet-sdk — agent-side wrapper SDK (NuGet `Clavenar.AgentSdk`, net8.0)

Wrap your Anthropic / OpenAI client; every tool call a model emits is inspected
by a Clavenar gateway against your policies *before* your agent executes it.
Sibling of the TypeScript (`@clavenar/agent-sdk`) and Python
(`clavenar-agent-sdk`) wrappers — same wire contract. `docs/PARITY.md` maps the
TS reference 1:1.

## Build, test, lint
```bash
dotnet restore
dotnet build -c Release            # --no-restore in CI
dotnet format --verify-no-changes  # CI gates on this; run dotnet format to fix
dotnet test -c Release             # --no-build in CI
dotnet list package --vulnerable --include-transitive
validation_root="$(mktemp -d)"
trap 'rm -rf "$validation_root"' EXIT
dotnet tool install --tool-path "$validation_root/tools" CycloneDX --version 6.2.0
"$validation_root/tools/dotnet-CycloneDX" Clavenar.AgentSdk.sln -o "$validation_root/sbom"
dotnet pack --no-build -c Release -o artifacts
```
Pinned to the exact .NET 8 SDK in `global.json` (`rollForward: disable`). CI
runs on self-hosted Linux plus GitHub-hosted `windows-latest` and
`macos-latest`, plus a self-hosted `sbom` job (`dotnet list package
--vulnerable --include-transitive` + CycloneDX). The
protected distribution workflow dispatches the exact csproj version and
signed-BOM source SHA to the NuGet/GitHub release workflow.

Run: library, no binary. The shippable package is
`src/Clavenar.AgentSdk/Clavenar.AgentSdk.csproj`; tests in
`tests/Clavenar.AgentSdk.Tests/`. Public API entry points:
`new ClavenarInspector(opts)` then `InspectAsync` / `InspectAllAsync` /
`EnforceAsync`, and the static facade `Clavenar.InspectResponseAsync(response,
opts)`. The SDK is an HTTP *client* of the gateway (example `Endpoint =
"http://localhost:8088"`); it does not listen on a port.

## Layout
- `src/Clavenar.AgentSdk/` — the package. Key files:
  - `ClavenarInspector.cs` — main surface: `InspectAsync`, `InspectAllAsync`, `EnforceAsync`, `InspectResponseAsync`, `PollPendingOnceAsync`.
  - `Clavenar.cs` — static `InspectResponseAsync` facade (wrap-and-forget over a provider response).
  - `ClavenarOptions.cs` — config; `Endpoint` is `required`, plus `Token`, `Mode`, `DevMode`, `OnVerdict`/`OnPolicyError`, `Timeout`, `HttpClient`, and `Retry` (`RetryOptions`). Resolve tuning is separate — `ResolveOptions` passed to `ClavenarPendingException.ResolveAsync`.
  - `Transport.cs` — `System.Text.Json` HTTP transport; `Verdict.cs` / `VerdictKind.cs` / `VerdictDetail.cs` / `VerdictContext.cs` — verdict model.
  - `GovernedExecutionClient.cs` — durable
    `clavenar.server-execution/v1` intent/effect/completion orchestration and
    uncertain-outcome recovery.
  - `SecureTransportProfile.cs` — reloadable mTLS/token transport profile;
    replaces the pooled client only after a complete valid credential snapshot.
  - `NormalizedToolCall.cs` — normalized `{name, id, arguments}`; throws `ClavenarConfigException` on unparseable args JSON.
  - `StreamGate.cs` — holds a tool call's closing event until a verdict returns (`Start`/`Update`/`CloseAsync`, `CloseByPrefixAsync`).
  - `Realtime.cs` — `Realtime.InspectAsync(FunctionCallDone, opts)` for realtime function-call events.
  - `DevMode.cs` / `Mode.cs` — stderr deny panel + enforce/observe enum.
  - `ClavenarException.cs` + `ClavenarDeniedException` /
    `ClavenarPendingException` / `ClavenarRateLimitedException` /
    `ClavenarRecoveryRequiredException` / `ClavenarTransportException` /
    `ClavenarConfigException`.
- `tests/Clavenar.AgentSdk.Tests/` — xUnit; `InternalsVisibleTo` grants internal access. `StubHandler.cs` / `Fixtures.cs` back transport tests.
- `examples/` — `semantic-kernel`, `native-openai`, `custom-dispatcher`, `realtime` (not packed/shipped).
- `fixtures/` — byte-identical `client-migration-v1` and `retry-separation-v1`
  (plus `sdk-cross-language-v1`) contracts; packed into the NuGet artifact and
  asserted by fixture tests.
- `docs/` — `SEQUENCES.md` (streaming/pending flows), `PARITY.md` (TS map).

## Conventions & invariants
- **Inspect before execute.** Every model `tool_use` must clear inspection before the agent runs it — that ordering is the SDK's whole contract. Don't add a path that dispatches a tool ahead of a verdict.
- **Duck-typing shape guard.** `InspectResponseAsync` duck-types an Anthropic message (`content[]` with `type:"tool_use"`) or an OpenAI completion (`choices[].message.tool_calls[]`, filtered to `type:"function"`) via JSON, then forwards the extracted calls to `InspectAllAsync`. A response that yields *zero* extracted calls is a no-op — `InspectAllAsync` early-returns on `calls.Count == 0` — which is the correct outcome for a text-only turn (no `tool_use` / `tool_calls` to gate). Don't add a throw here; a model turn with no tool calls is normal, not a contract error. The one drift signal: a turn whose `stop_reason` / `finish_reason` declares tool use but extracts zero calls emits a `Trace.TraceWarning` (provider shape drift; the calls were not inspected).
- **No provider dependency.** Only the in-box `System.Text.Json`. Never add a PackageReference to the OpenAI/Anthropic SDKs — duck-type the JSON instead. Keeps the supply-chain surface minimal.
- **Fail-closed.** In enforce `Mode`, a transport failure throws `ClavenarTransportException` (`Status == 0` = network); it must not fall through to allow. `Mode.Observe` is the only non-blocking path — verdicts via `OnVerdict`, errors via `OnPolicyError`.
- **Decision and execution retries are separate.** Side-effect-free decision
  calls may retry transient transport failures. A durable execution is keyed by
  its idempotency ID; an uncertain effect must recover state or raise
  `ClavenarRecoveryRequiredException`, never blindly execute again.
- **Verdict → exception mapping is load-bearing.** `Allow`/`Deny`/`Pending`/`RateLimited` from `InspectAsync`; enforce/batch paths raise `ClavenarDeniedException` (carries `Reasons`, `Layer`, `IntentCategory`, `CorrelationId`, optional per-detector `Detail`), `ClavenarPendingException` (`await ResolveAsync()`), `ClavenarRateLimitedException` (429 before evaluation; `Code` = `rate_limited`/`quota_exceeded`, optional `RetryAfterSecs`; a verdict — never retried), `ClavenarTransportException`, `ClavenarConfigException`.
- **`DevMode = true` is dev/staging only.** It renders the detailed deny panel (per-detector scores) to stderr; detailed denials are an attacker oracle in prod. `Detail` is null unless the gateway opts in (`CLAVENAR_PROXY_VERBOSE_VERDICTS=true`).
- **No secrets at rest.** Hold only `Endpoint` + optional bearer `Token`, supplied per process by the caller.

C# / .NET rules that bite here:
- `TreatWarningsAsErrors=true` + `EnableNETAnalyzers=true` — warnings fail the build; fix the code, don't suppress.
- `Nullable=enable`, `ImplicitUsings=disable` — annotate nullability; write explicit `using`s.
- `dotnet format --verify-no-changes` is a CI gate. Follow `.editorconfig`: 4-space C#, brace-on-new-line; 2-space for csproj/json/yml; LF, final newline.
- Public types/members carry XML doc comments (`GenerateDocumentationFile=true`; only CS1591 is waived).
- Anything in a `public` signature must itself be `public` (option/verdict/exception types are part of the surface).
- Bump csproj `<Version>` for any shipped change; the release tag must equal it or the publish job fails.
- Commit subjects must start with a lowercase letter.

## Pointers

[README](README.md) · [security policy](SECURITY.md) ·
[contributing](CONTRIBUTING.md) · [sequence diagrams](docs/SEQUENCES.md) ·
[SDK parity](docs/PARITY.md).
