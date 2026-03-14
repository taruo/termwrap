$ErrorActionPreference = "Stop"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

& $csc /nologo /target:exe /out:termwrap.exe Program_v1_0_1.cs SessionSupport_v1_0_1.cs TelnetTransport_v1_0_1.cs PipeSecurityFactory_v1_0_1.cs
