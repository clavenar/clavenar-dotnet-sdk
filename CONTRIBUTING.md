# Contributing

## Verify before pushing

```bash
dotnet format --verify-no-changes        # style gate (CI parity)
dotnet build -c Release                   # warnings-as-errors
dotnet test -c Release                    # xUnit suite
dotnet pack -c Release -o artifacts       # produces .nupkg + .snupkg
```

CI runs the build + test on ubuntu / windows / macOS and emits a CycloneDX
SBOM.

## Conventions

- `net8.0`; no provider dependency — the Anthropic / OpenAI response shapes
  are duck-typed via `System.Text.Json`.
- Behavior must stay 1:1 with the TypeScript reference on the wire — if a
  change touches wire behavior, update `docs/PARITY.md` and add a test.
- Tests run against a custom `HttpMessageHandler` (`StubHandler`); no live
  network in unit tests.

## Releasing to NuGet

One-time setup: a NuGet.org account, reserve the `Clavenar.*` package-id
prefix (needs the verified `clavenar.com` domain), and add a
`NUGET_API_KEY` repository secret (migrate to NuGet.org OIDC
trusted-publishing as a follow-up). Then push a tag matching the csproj
`<Version>` (e.g. `v1.0.0`): `release.yml` asserts the match, builds,
tests, packs (with `.snupkg` symbols + SourceLink), and pushes.
