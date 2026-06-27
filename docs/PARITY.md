# Behavior parity

The .NET SDK reproduces the TypeScript reference
([`@clavenar/agent-sdk`](https://github.com/clavenar/clavenar-typescript-sdk))
byte-for-byte on the wire. These behaviors are identical across the TS,
Python, and .NET SDKs:

| Behavior | Contract |
|---|---|
| Inspect request | `POST {endpoint}/mcp`, JSON-RPC 2.0 `{jsonrpc,method:"tools/call",params:{name,arguments},id}`; `arguments` forwarded verbatim |
| Auth | `Authorization: Bearer {token}` only when a token is set |
| 200 | allow; `X-Clavenar-Correlation-Id` surfaced when present |
| 403 | deny; missing `reasons`/`review_reasons` → empty, missing `intent_category` → `""`; non-string `error` → transport error |
| 202 | pending; `CorrelationId = header ?? body`, both empty → transport error |
| Retry | network + 5xx retry up to `MaxAttempts` (default 3); full-jitter backoff `base*2^attempt*(0.5+rand*0.5)`, base 100ms; 200/403/other-4xx never retry; timeout 10s |
| Inspect-all | concurrent inspect, **submission-order** first-deny; `OnVerdict` before any deny→throw |
| Enforce | first deny → `ClavenarDeniedException`, pending → `ClavenarPendingException`; transport error fails closed, `OnPolicyError` not called |
| Observe | nothing blocks; per-call transport failure → `OnPolicyError`, treated as allowed |
| Response extraction | `InspectResponseAsync` duck-types Anthropic `content[]` (`type:"tool_use"`) or OpenAI `choices[].message.tool_calls[]` (filtered to `type:"function"`); zero extracted calls → no-op (text-only response) |
| Streaming | closing event held until verdict; empty args → `{}`; unparseable drained args → `ClavenarConfigException` |
| Resolve | poll `GET /pending/{id}` every 2s, ceiling 10m; deny → `ClavenarDeniedException` (`IntentCategory="PendingDenied"`, reason = decider note or `"operator denied"`); 401/404 terminal; 5xx/network swallowed |
| OpenAI non-streaming, unparseable args | `ClavenarConfigException` (matches TS, not Python's raw-string fallback) |
| Realtime | `arguments` forwarded as a raw JSON string on parse failure |
| URL join | trims one trailing/leading slash; never drops a base path like `https://gw/clavenar` |

## Intentional, additive .NET-idiom differences

None change wire bytes or verdict outcomes:

1. **`ClavenarException` base class.** TS/Python root their four errors at
   the language base; .NET adds a `ClavenarException` root so callers can
   `catch (ClavenarException)`. Each concrete type keeps the same name and
   fields.
2. **Task-based async throughout.** Every call is `…Async` and takes a
   `CancellationToken`; the per-request timeout is a linked
   `CancellationTokenSource`, and `ResolveAsync` uses a monotonic
   `Stopwatch` deadline.
3. **No transparent client proxy.** The `OpenAI` / `Anthropic.SDK` clients
   are sealed concrete classes, so there is no `DispatchProxy` wrap.
   Instead `ClavenarInspector.EnforceAsync` (tool-dispatch boundary) and
   `Clavenar.InspectResponseAsync` (duck-typed response) cover the same
   ground without a provider dependency.
4. **No `extraHeaders` option** — matches the TS reference (the Python SDK
   has one; the .NET SDK follows TS).
