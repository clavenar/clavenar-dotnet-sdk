# Security

## Reporting a vulnerability

Email **security@clavenar.com** with details and a reproduction. Please do
not open a public issue for security reports. We aim to acknowledge within
two business days.

## Posture

- **No provider dependency.** The package depends only on the in-box
  `System.Text.Json`; it duck-types the Anthropic / OpenAI response shapes
  rather than pulling their SDKs, keeping the supply-chain surface minimal.
- **Fail-closed by default.** In enforce mode a transport failure to reach
  clavenar throws `ClavenarTransportException` rather than silently
  allowing the call.
- **No secrets at rest.** The SDK holds only the endpoint URL and an
  optional bearer token, both supplied by the caller per process.
- **Supply chain.** CI runs `dotnet list package --vulnerable` and emits a
  CycloneDX SBOM; releases ship deterministic builds with SourceLink and a
  symbol package.

## Supported versions

The latest minor release receives security fixes.
