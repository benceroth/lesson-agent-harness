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
- New attack: add it to `src/AtlasDesk.ScriptedModel/AttackCatalog.cs`, update the counts in `docs/harness-vs-prompt.md` from `compare --markdown`, then run `scripts\regenerate-samples.ps1` (the AttackSuite tests check the doc and committed output).
- Before you open a PR, do the mutation check: temporarily disable the control you changed and confirm a test fails.
- Warnings are errors. No real data, secrets or live-model calls in tests.
- Keep the claims honest: "on this sample", not "secure".

## Add your own attack

Start with [`failure-first/attack-template.md`](failure-first/attack-template.md). Add the case to `AttackCatalog`, write a deterministic test that proves the harness blocks it, and run `dotnet test` plus `scripts\regenerate-samples.ps1`. Update the threat-model row and comparison counts from the generated output. Keep all data synthetic; if a test intentionally contains a secret-shaped sentinel, keep it in an already-allowlisted test file or extend only the relevant test path in `.gitleaks.toml`. Never put real credentials in a fixture or widen the allowlist to production files.
