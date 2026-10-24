param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outputDirectory = Join-Path $root 'artifacts'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$output = Join-Path $outputDirectory 'ClientInstallTests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /reference:System.Core.dll /reference:System.Web.Extensions.dll "/out:$output" (Join-Path $root 'src\Backend.cs') (Join-Path $PSScriptRoot 'ClientInstallTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Client installation tests failed to compile.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Client installation tests failed.' }
if ($Live) {
    & $output --live
    if ($LASTEXITCODE -ne 0) { throw 'Running client detection failed.' }
}
