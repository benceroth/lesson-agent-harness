# Contributing

Thanks for helping. Keep changes small and tested.

## Setup

- .NET SDK 10.0.201 or newer feature band (see `global.json`).
- No API keys or network services are needed; all tests are deterministic.

```powershell
dotnet build
dotnet test
dotnet run --project src\AtlasDesk.Sample -- compare
```

## Rules

- Every control must cite a test in `docs/threat-model.md`, and the test name must exist. Add the row when you add the control.
- New attack: add it to `src/AtlasDesk.ScriptedModel/AttackCatalog.cs`, then update the counts in `docs/harness-vs-prompt.md` from the real output of `compare --markdown` (the AttackSuite test checks the doc).
- Before you open a PR, do the mutation check: temporarily disable the control you changed and confirm a test fails.
- Warnings are errors. No real data, secrets or live-model calls in tests.
- Keep the claims honest: "on this sample", not "secure".
