param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$payload = Join-Path $outputRoot 'payload.zip'
$stream = [IO.File]::Open($payload, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('助手.ps1', '界面.ps1', 'main.xaml', 'close.xaml', 'settings.xaml', 'app.ico')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $outputRoot 'OPPO-Mouse-sharing.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$root\app.ico" "/resource:$payload,payload.zip" "/out:$output" (Join-Path $PSScriptRoot 'Launcher.cs') (Join-Path $PSScriptRoot 'Backend.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Write-Output $output
