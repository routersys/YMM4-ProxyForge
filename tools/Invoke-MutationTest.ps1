#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Filter = '',
    [switch] $StopOnSurvivor
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$definitionPath = Join-Path $PSScriptRoot 'mutations.json'
$testProject = Join-Path $root 'ProxyForge.Tests/ProxyForge.Tests.csproj'

$mutations = Get-Content -Raw -Encoding utf8 $definitionPath | ConvertFrom-Json
if ($Filter) {
    $mutations = $mutations | Where-Object { $_.name -like "*$Filter*" }
}

function Invoke-TestSuite {
    $output = & dotnet test $testProject --nologo -v:q 2>&1
    return @{
        Succeeded = $LASTEXITCODE -eq 0
        Output    = ($output -join [Environment]::NewLine)
    }
}

Write-Host '基準の試験を実行します。'
$baseline = Invoke-TestSuite
if (-not $baseline.Succeeded) {
    Write-Host $baseline.Output
    throw '変異を入れる前の試験が失敗しています。先にそちらを直してください。'
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($mutation in $mutations) {
    $target = Join-Path $root $mutation.file
    if (-not (Test-Path $target)) {
        throw "対象が見つかりません: $($mutation.file)"
    }

    $original = Get-Content -Raw -Encoding utf8 $target
    if (-not $original.Contains($mutation.find)) {
        throw "置換元が見つかりません: $($mutation.name) / $($mutation.file)"
    }

    $mutated = $original.Replace($mutation.find, $mutation.replace)
    if ($mutated -eq $original) {
        throw "置換が効いていません: $($mutation.name)"
    }

    try {
        Set-Content -NoNewline -Encoding utf8 -Path $target -Value $mutated
        $run = Invoke-TestSuite
        $killed = -not $run.Succeeded
    }
    finally {
        Set-Content -NoNewline -Encoding utf8 -Path $target -Value $original
    }

    $results.Add([pscustomobject]@{
        Name   = $mutation.name
        File   = Split-Path -Leaf $mutation.file
        Killed = $killed
    })

    $mark = if ($killed) { '殺した  ' } else { '生存    ' }
    Write-Host "$mark $($mutation.name)"

    if ($StopOnSurvivor -and -not $killed) {
        break
    }
}

$total = $results.Count
$killedCount = ($results | Where-Object Killed).Count
$score = if ($total -eq 0) { 0 } else { [math]::Round(100.0 * $killedCount / $total, 2) }

Write-Host ''
Write-Host "変異 $total 件のうち $killedCount 件を検知しました。検知率 $score %"

$survivors = $results | Where-Object { -not $_.Killed }
if ($survivors) {
    Write-Host ''
    Write-Host '検知できなかった変異:'
    $survivors | ForEach-Object { Write-Host "  $($_.File) : $($_.Name)" }
}

Write-Host ''
& dotnet build (Join-Path $root 'ProxyForge.slnx') -v:q --nologo | Out-Null

exit $(if ($survivors) { 1 } else { 0 })
