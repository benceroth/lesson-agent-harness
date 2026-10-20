param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\..").Path,
    [double]$Threshold = 90
)

# Enforces a line-coverage threshold on the Approvals and Limits namespaces
# using the Cobertura files that `dotnet test --collect:"XPlat Code Coverage"` writes.
$files = Get-ChildItem -Path $Root -Recurse -Filter coverage.cobertura.xml
if (-not $files) { Write-Error "No coverage files found. Run dotnet test --collect:'XPlat Code Coverage' first."; exit 2 }

$covered = @{}; $total = @{}
foreach ($f in $files) {
    [xml]$xml = Get-Content $f.FullName
    foreach ($class in $xml.SelectNodes("//class")) {
        if ($class.filename -notmatch 'AtlasDesk\.Harness') { continue }
        $area = if ($class.filename -match '[\\/]Approvals[\\/]') { 'Approvals' } elseif ($class.filename -match '[\\/]Limits[\\/]') { 'Limits' } else { continue }
        foreach ($line in $class.SelectNodes("lines/line")) {
            $key = "$area|$($class.filename)|$($line.number)"
            $total[$key] = $true
            if ([int]$line.hits -gt 0) { $covered[$key] = $true }
        }
    }
}

$failed = $false
foreach ($area in 'Approvals', 'Limits') {
    $t = ($total.Keys | Where-Object { $_ -like "$area|*" }).Count
    $c = ($covered.Keys | Where-Object { $_ -like "$area|*" }).Count
    if ($t -eq 0) { Write-Host "${area}: no lines found"; $failed = $true; continue }
    $pct = [math]::Round(100.0 * $c / $t, 1)
    Write-Host "$area line coverage (all test projects combined): $pct% ($c/$t), threshold $Threshold%"
    if ($pct -lt $Threshold) { $failed = $true }
}
if ($failed) { exit 1 }
