$ErrorActionPreference = "Stop"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

& $csc /nologo /target:exe /out:termwrap.exe Program.cs SessionSupport.cs TelnetTransport.cs PipeSecurityFactory.cs
