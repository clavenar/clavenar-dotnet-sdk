# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/) and
the project adheres to [Semantic Versioning](https://semver.org/).

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
