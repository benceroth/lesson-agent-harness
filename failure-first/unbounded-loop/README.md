# Unbounded tool loop

**Attack:** the model repeats the same read-only tool call forever.

**Expected block:** the run ends with `turn_limit`, is not reported as success, and does not exceed the configured step budget.

**Reproduce:** `dotnet test tests\Limits.Tests\Limits.Tests.csproj --filter "FullyQualifiedName~Infinite_tool_loop_is_stopped_at_step_limit"`

**Fix:** enforce the step budget in the runner before another model request. Keep terminal statuses machine-readable and immutable.
