# Failure-first: break the harness before extending it

These are small, deterministic attacks against the controls in this sample. Run all four from the repository root:

```powershell
.\failure-first\Run.ps1
```

Each case names the expected block, the test that proves it, and the control to change when investigating a failure. A green test means this specific scenario was blocked; it is not a general security guarantee.

To add a scenario, copy [`attack-template.md`](attack-template.md), keep the input synthetic, add a test, then update `AttackCatalog` and regenerate the samples if it belongs in the comparison.
