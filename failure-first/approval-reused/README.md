# Approval reused for a changed action

**Attack:** reuse approval for a `$10.00` refund on a changed `$499.00` action.

**Expected block:** `ActionMismatch`; no refund side effect. The approval must match the hash of the exact run, tool, and canonical arguments.

**Reproduce:** `dotnet test tests\Approvals.Tests\Approvals.Tests.csproj --filter "FullyQualifiedName~Changed_arguments_invalidate_the_approval"`

**Fix:** keep approval bound to the canonical action hash and consume it once. Do not replace the hash comparison with a reusable `approved = true` flag.
