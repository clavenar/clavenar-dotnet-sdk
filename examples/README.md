# Examples

Two ways to integrate, mirroring the TypeScript and Python SDKs:

1. **Inspect at the tool-dispatch boundary** (recommended) — build
   `NormalizedToolCall`s and inspect before running the tools. See
   [`custom-dispatcher`](custom-dispatcher) and
   [`semantic-kernel`](semantic-kernel) (an `IFunctionInvocationFilter`
   that gates every SK function call).
2. **Inspect a provider result** — extract the tool calls from a provider
   completion and enforce them. See [`native-openai`](native-openai).

Plus [`realtime`](realtime) for the OpenAI Realtime websocket surface.

Each example references the SDK by project (no NuGet needed). Run one
(reads `CLAVENAR_ENDPOINT`, default a local
[clavenar-lite](https://github.com/clavenar/clavenar-lite) at
`http://localhost:8088`):

```bash
dotnet run --project examples/custom-dispatcher
```
