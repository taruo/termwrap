$ErrorActionPreference = "Stop"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

Push-Location $PSScriptRoot
try {
    & $csc /nologo /warn:4 /warnaserror+ /target:exe /r:System.Web.Extensions.dll /out:termwrap.exe Program.cs CliOptions.cs CliOutput.cs SessionSupport.cs TelnetTransport.cs PipeSecurityFactory.cs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}
