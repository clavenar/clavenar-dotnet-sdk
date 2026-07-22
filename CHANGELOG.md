# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/) and
the project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.5.0] - 2026-07-21

### Changed

- Package the exact `clavenar.client-migration/v1` fixture and schema and
  document the client-first rollout. Inspection remains an explicit
  side-effect-free decision with its canonical pre-network request ID.

## [1.4.0] - 2026-07-21

### Changed

- Automatic transport retry is explicitly confined to the side-effect-free
  decision request and retains its original canonical idempotency ID. Registered
  executor failures are never retried.
- The shared `clavenar.retry-separation/v1` fixture and schema are packaged in
  the NuGet package.

## [1.3.0] - 2026-07-21

### Added

- `GovernedExecutionClient` with serializable prepared requests, a registered
  executor, durable intent/completion store, workload receipt signer, and
  actual provider-result return.
- The shared `clavenar.sdk-cross-language/v1` fixture, packaged in the NuGet.

### Changed

- Inspection explicitly selects `clavenar.decision/v1` with a UUID allocated
  before the first attempt and retained across safe retries. Multi-tool turns
  use one ordered atomic decision.

### Added

- 429 rate-limit verdicts. An HTTP 429 from the gateway now parses into
  a `VerdictKind.RateLimited` verdict carrying the gate code
  (`rate_limited` request-velocity or `quota_exceeded` per-tenant
  spend), the gateway's `reasons`, and the optional `retry_after_secs`,
  instead of collapsing into a generic transport error. Enforce mode
  throws the new `ClavenarRateLimitedException`; observe mode passes the
  call through and surfaces the verdict via `OnVerdict`. Like 403, a
  429 is a verdict — the transport never retries it.
- Shape-drift signal: `InspectResponseAsync` on a response whose
  `stop_reason` / `finish_reason` declares tool use but from which zero
  tool calls were extracted emits a `Trace.TraceWarning` — extraction
  stays a no-op for text-only turns, but silent provider-shape drift is
  now visible.

## [1.1.0]

### Added

- **Dev-mode deny rendering.** Setting `ClavenarOptions.DevMode = true`
  writes a readable per-detector panel to stderr when a tool call is
  denied; `DevMode.RenderDenyPanel(e)` returns the same string directly.
  Dev/staging only — detailed denials are an attacker oracle.

## [1.0.0]

Initial release. .NET port of the Clavenar agent-wrapper SDK,
behavior-compatible with `@clavenar/agent-sdk` (TypeScript) and
`clavenar-agent-sdk` (Python) on the wire.

### Added

- `ClavenarInspector` — `InspectAsync` / `InspectAllAsync` /
  `EnforceAsync` / `PollPendingOnceAsync` / `InspectResponseAsync`, the
  primary surface for Semantic Kernel filters and custom tool dispatchers.
- `Clavenar.InspectResponseAsync` — duck-types an Anthropic message or an
  OpenAI chat completion (no provider dependency).
- `StreamGate` streaming primitive, `Realtime` helper,
  `ClavenarPendingException.ResolveAsync`, enforce / observe modes with
  `OnVerdict` / `OnPolicyError`, retries with full-jitter backoff.
- Exception hierarchy rooted at `ClavenarException`
  (`ClavenarDeniedException` / `ClavenarPendingException` /
  `ClavenarConfigException` / `ClavenarTransportException`).

### Notes

- Matches the TypeScript reference where TS and Python diverge: an OpenAI
  non-streaming tool call with unparseable `arguments` throws
  `ClavenarConfigException`. See `docs/PARITY.md`.
- Targets `net8.0`; the only dependency is the in-box `System.Text.Json`.
