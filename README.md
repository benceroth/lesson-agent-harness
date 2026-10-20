# AtlasDesk agent harness

**Question:** if a model is wrong, hijacked or stuck, what stops it from doing damage? **Answer in this repo:** a small .NET harness around the loop (validation, allow-listed tools, approvals bound to the exact action, run limits, resumable state, redacting telemetry), each control tied to a test.

On the scripted attack set in this sample, a naive loop allowed **12 of 12** attacks; the harness allowed **0 of 12** and still completed the 2 benign control cases. Measured on this sample, not a benchmark. See [docs/harness-vs-prompt.md](docs/harness-vs-prompt.md).

## Quickstart

Requires the .NET SDK (see `global.json`). No API keys, no network.

```powershell
dotnet build
dotnet test                                              # 134 tests
dotnet run --project src\AtlasDesk.Sample -- compare         # naive loop vs harness on the attack set
dotnet run --project src\AtlasDesk.Sample -- approval-trace  # exact-action approval and tamper rejection
dotnet run --project src\AtlasDesk.Sample -- demo            # interactive approval demo (SQLite state)
.\scripts\regenerate-samples.ps1                           # refresh the committed comparison and approval trace
```

## Layout

```text
src/AtlasDesk.Harness/        reusable library: Validation, Tools, Limits, Approvals, State, Telemetry, Runtime
src/AtlasDesk.ScriptedModel/  fake model that replays scripted (malicious/broken) outputs; attack catalog
src/AtlasDesk.Sample/         support-desk agent, naive loop, comparison runner
tests/                        Validation, Limits, Approvals, State, Telemetry, Injection, AttackSuite
docs/                         threat-model, harness-vs-prompt, what-to-log, implementation-sequence
failure-first/                focused break-it scenarios, runner, and attack template
samples/                      generated attack comparison and exact-action approval trace
```

## How it works

The model proposes; the harness decides. Each proposed action is parsed strictly, matched exactly against the tool allow-list, checked against budgets, approved if it has side effects (the approval is bound to a SHA-256 of run id, tool and canonical arguments, is single-use and expires), executed with a timeout, and recorded in a run store so a crash can resume without repeating effects. Tool output reaches the model only as marked-untrusted data.

[docs/threat-model.md](docs/threat-model.md) maps every threat to a control and to the real test that proves it.

## Known limits

- It is a sample, not a framework or a security guarantee. The scripted set is small and written by the harness author.
- Approval grants live in memory; after a restart an action needs re-approval.
- A tool that ignores cancellation is abandoned, not killed.
- The state store keeps tool output (possibly PII); protect the SQLite file. Redaction covers telemetry only and is pattern-based.
- No live model is used anywhere, so real-model behaviour is not covered.

More in [docs/threat-model.md](docs/threat-model.md).


## Failure-first: now break it

Run four focused attacks before extending the harness: changed-action approval reuse, secret leakage into logs, an unbounded tool loop, and an injected instruction in tool output. Each [scenario](failure-first/) names the expected block and points to the test and control that enforce it.

```powershell
.\failure-first\Run.ps1
```

## Extend it

1. Add an attack to `src/AtlasDesk.ScriptedModel/AttackCatalog.cs`; prove the naive loop allows it and the harness blocks it.
2. Add a side-effecting tool with a narrow schema and action-bound approval; test changed arguments and replay before wiring it into the runner.
3. Add a structured run-state field, persist it through SQLite, and verify a resumed model request uses it without relying on transcript text.

Refresh the committed generated samples with `scripts\regenerate-samples.ps1` after changing the comparison or approval flow.

## Read the story

Companion to the post [Your AI Agent Needs a Harness Before It Needs Better Prompts](https://broth.hu/).

## License

MIT. See [SECURITY.md](SECURITY.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

<sub>[Bence Roth](https://broth.hu/)</sub>
