param([switch]$KeepArtifacts)

$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found: $csc"
}

# Never replace or execute the installed termwrap.exe: it may host real sessions.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("termwrap-cli-tests-" + [Guid]::NewGuid().ToString("N"))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$buildRoot = Join-Path $testRoot "build"
$caseRoot = Join-Path $testRoot "cases"
$application = Join-Path $buildRoot "termwrap.exe"
$harness = Join-Path $buildRoot "CliRegression.exe"
$testExitCode = 1
New-Item -ItemType Directory -Path $buildRoot, $caseRoot | Out-Null

try {
    $sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.cs" -File |
        Sort-Object Name | ForEach-Object { $_.FullName })
    $applicationArguments = @("/nologo", "/target:exe", "/r:System.Web.Extensions.dll", "/out:$application") + $sources
    & $csc @applicationArguments
    if ($LASTEXITCODE -ne 0) { throw "Application compilation failed ($LASTEXITCODE)" }

    & $csc /nologo /target:exe /r:System.Web.Extensions.dll "/out:$harness" (Join-Path $PSScriptRoot "tests\CliRegression.cs")
    if ($LASTEXITCODE -ne 0) { throw "Regression harness compilation failed ($LASTEXITCODE)" }

    Write-Host ("Test artifacts: " + $testRoot)
    & $harness $application $caseRoot
    $testExitCode = $LASTEXITCODE
}
catch {
    Write-Error -ErrorAction Continue $_
}
finally {
    if ($testExitCode -eq 0 -and -not $KeepArtifacts) {
        # Resolve and check the exact directory before recursively deleting it.
        $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).ProviderPath
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.Equals($testRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTestRoot) -notmatch '^termwrap-cli-tests-[0-9a-f]{32}$') {
            throw "Refusing cleanup of unexpected path: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
    else {
        Write-Host ("Artifacts retained: " + $testRoot)
    }
}
exit $testExitCode
