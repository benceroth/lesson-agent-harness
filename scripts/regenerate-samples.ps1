$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    dotnet build --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

    $comparison = & dotnet run --no-build --project src\AtlasDesk.Sample -- compare --markdown
    if ($LASTEXITCODE -ne 0) { throw 'Attack comparison failed.' }
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'samples\attack-comparison.md'), (($comparison -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

    $trace = & dotnet run --no-build --project src\AtlasDesk.Sample -- approval-trace
    if ($LASTEXITCODE -ne 0) { throw 'Exact-action approval trace failed.' }
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'samples\exact-action-approval.md'), (($trace -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

    Write-Host 'Updated samples\attack-comparison.md and samples\exact-action-approval.md.'
}
finally {
    Pop-Location
}
