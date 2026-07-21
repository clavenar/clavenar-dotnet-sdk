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

1. Allocate one client UUID before network access.
2. Submit the complete ordered sibling set through
   `clavenar.atomic-tool-call-batch/v1` as one side-effect-free decision.
3. Process the batch verdict in **submission order**: `OnVerdict` fires per call, then the
   first `Deny` → `ClavenarDeniedException` / `Pending` →
   `ClavenarPendingException`. Observe never throws; a batch transport
   failure fires `OnPolicyError` for every covered call.

## Response extraction — `InspectResponseAsync`

`ClavenarInspector.InspectResponseAsync` (and the static
`Clavenar.InspectResponseAsync` facade over it) is the headline,
wrap-and-forget entry point: hand it a whole provider response and it
inspects every tool call the model emitted, with no provider SDK
dependency.

1. Serialize the response object to a JSON tree
   (`JsonSerializer.SerializeToNode`) — the shape is duck-typed, so any
   object that serializes to the expected JSON works.
2. `ExtractCalls` walks the tree for whichever shape it recognizes:
   - **Anthropic** — a `content[]` array; each block with
     `type:"tool_use"` becomes a `NormalizedToolCall` from its `id` /
     `name` / `input` (the `input` node is deep-cloned).
   - **OpenAI** — a `choices[]` array; each
     `choices[].message.tool_calls[]` entry **filtered to
     `type:"function"`** becomes a `NormalizedToolCall` from its `id` /
     `function.name` / `function.arguments` (arguments are a
     JSON-encoded string, parsed via
     `NormalizedToolCall.FromJsonArguments`).
3. The extracted calls are forwarded to `InspectAllAsync`, so the
   submission-order, fail-closed enforce semantics from **Batch inspect**
   apply unchanged.

A text-only response carries no `tool_use` / `tool_calls`, so
`ExtractCalls` returns an empty list and `InspectAllAsync` early-returns
on `calls.Count == 0` — a silent no-op. That is the correct outcome: a
model turn with nothing to run has nothing to gate.

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
