# What to log

The harness emits spans from `ActivitySource` `AtlasDesk.Harness` (compatible with OpenTelemetry) and redacted logs through `ILogger`.

| Span | Fields |
|---|---|
| `harness.run` | `harness.run.id`, `harness.run.resumed`, `harness.status` (named terminal state), `harness.steps`, `harness.tokens.total` |
| `harness.step` | `harness.step`, `harness.tokens`, `harness.action.type`, `harness.validation.result` (code, never content) |
| `harness.tool` | `tool.name`, `tool.attempt`, `tool.latency_ms`, `tool.outcome`, `harness.policy.decision` (`allow` or `allow:approved`) |

## Never logged

- Tool arguments or results, email bodies, customer names, free-text model output.
- Names proposed by the model that are not in the registry (they may contain injected content).
- Anything matching the redaction patterns (emails, bearer tokens, API keys, `password=` style pairs, card and phone numbers).

Two layers: `Tag(...)` redacts at write time, and `RedactingActivityProcessor` scrubs string tags and drops denylisted keys (`tool.args`, `customer.*`, ...) for any exporter pipeline. Verified in `tests/Telemetry.Tests`.

To export, add the source to your OpenTelemetry builder:

```csharp
builder.AddSource("AtlasDesk.Harness").AddProcessor(new RedactingActivityProcessor());
```

Add the processor before exporters.
