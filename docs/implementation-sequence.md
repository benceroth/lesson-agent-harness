# Implementation sequence

Build the harness in this order; each step is independently testable and earns its place before the next.

1. **Run state and named terminal states.** Know what "stopped" means (`RunState`, `RunStatus`). Never report a stopped run as success.
2. **Validation and tool allow-list.** Nothing the model says reaches a tool unvalidated.
3. **Limits.** Steps, tool attempts, tokens, cost, deadline, tool timeout, retry cap, invalid-output cap.
4. **Approvals** for side-effecting tools, bound to the action hash, single-use, expiring.
5. **Idempotency** for side effects, so retries and resumes cannot repeat them.
6. **State store** (in-memory, then SQLite) and resume.
7. **Redacting telemetry.** Spans and logs that are useful and safe.
8. **Attack suite.** A scripted model that misbehaves on purpose, run against an unprotected loop and the harness.
9. **Mutation check.** Disable a control; a test must fail. If none does, the control is untested.
