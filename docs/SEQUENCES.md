# Sequences

How the SDK behaves on each wire path. It is a client of
[clavenar-lite](https://github.com/clavenar/clavenar-lite)'s
`POST /mcp` + `GET /pending/{id}` surface.

## Single inspect — `ClavenarInspector.InspectAsync`

1. Serialize the JSON-RPC envelope `{jsonrpc,method:"tools/call",params:{name,arguments},id}`.
2. `POST {endpoint}/mcp` with a per-request timeout (default 10s) from a
   linked `CancellationTokenSource`.
3. Map the response: `200` → allow (read `X-Clavenar-Correlation-Id`),
   `403` → deny (normalize the envelope), `202` → pending
   (`CorrelationId = header ?? body`), anything else → transport error.
4. Network errors and 5xx retry up to `MaxAttempts` with full-jitter
   backoff; `200` / `403` / other `4xx` never retry.

`InspectAsync` returns a `Verdict` and never throws on a deny.

## Batch inspect — `InspectAllAsync` / `EnforceAsync`

1. Fan out one inspection per call (`Task.WhenAll`).
2. In enforce mode, any transport error surfaces before any deny is
   processed (fail closed), matching Promise.all semantics.
3. Process in **submission order**: `OnVerdict` fires per call, then the
   first `Deny` → `ClavenarDeniedException` / `Pending` →
   `ClavenarPendingException`. Observe never throws; a per-call transport
   failure fires `OnPolicyError` and is treated as allowed.

## Streaming gate — `StreamGate`

Driven from the streaming loop:

1. Tool-call opening → `Start(key, id, name)`.
2. Argument fragments → `Update(key, …)`.
3. The closing event → `CloseAsync(key)` / `CloseByPrefixAsync`
   **before** it is forwarded. The gate assembles the buffered call(s) and
   inspects them, throwing on an enforce-mode deny so the wrapper stops the
   stream before releasing the closing event. Empty arguments assemble to
   `{}`; unparseable arguments throw `ClavenarConfigException`.

## Pending resolve — `ClavenarPendingException.ResolveAsync`

1. Poll `GET /pending/{id}` every poll interval (default 2s) until the
   deadline (default 10m), using a monotonic `Stopwatch`.
2. `decision:"allow"` → return; `decision:"deny"` →
   `ClavenarDeniedException` (`IntentCategory="PendingDenied"`, reason =
   decider note or `"operator denied"`).
3. `401` / `404` are terminal; `5xx` and network blips are swallowed.

## Realtime — `Realtime.InspectAsync`

Normalize a `response.function_call_arguments.done` event into a
`NormalizedToolCall` (arguments forwarded as a raw JSON string if they
don't parse) and run a single inspect.
