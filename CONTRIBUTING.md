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

## Releasing

Direct tag publication is disabled. The protected stack distribution workflow
dispatches `release.yml` with the exact signed-BOM source SHA and component
version. The workflow builds and tests, publishes the `.nupkg` and `.snupkg`
to the authenticated GitHub Packages NuGet registry, and attaches both to an
anonymous versioned GitHub release. Missing or substituted protected inputs
fail before publication.
