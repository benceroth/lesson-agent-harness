$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $checks = @(
        @{ Name = 'changed-action approval reuse'; Project = 'tests\Approvals.Tests\Approvals.Tests.csproj'; Filter = 'FullyQualifiedName~Changed_arguments_invalidate_the_approval' },
        @{ Name = 'secret redaction in logs'; Project = 'tests\Telemetry.Tests\Telemetry.Tests.csproj'; Filter = 'FullyQualifiedName~Logs_never_contain_secrets_or_pii' },
        @{ Name = 'unbounded tool loop'; Project = 'tests\Limits.Tests\Limits.Tests.csproj'; Filter = 'FullyQualifiedName~Infinite_tool_loop_is_stopped_at_step_limit' },
        @{ Name = 'injected instruction in tool output'; Project = 'tests\Injection.Tests\Injection.Tests.csproj'; Filter = 'FullyQualifiedName~Injected_refund_proposal_is_denied_without_approval' }
    )
    foreach ($check in $checks) {
        Write-Host "`n=== $($check.Name) ==="
        dotnet test $check.Project --filter $check.Filter
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
