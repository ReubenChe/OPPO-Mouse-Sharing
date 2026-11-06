param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path ([IO.Path]::GetTempPath()) ('oppo-updater-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
try {
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $wpf = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\WPF'
    $output = Join-Path $directory 'UpdaterTests.exe'
    $fixtureSource = Join-Path $directory 'fixture.cs'
    $fixture = Join-Path $directory 'fixture.exe'
    [IO.File]::WriteAllText($fixtureSource, 'using System; using System.IO; class Fixture { static void Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "started.txt"), "ok"); } }')
    & $compiler /nologo /target:winexe "/out:$fixture" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw 'Update fixture failed to compile.' }
    & $compiler /nologo /target:exe /platform:x64 /codepage:65001 /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Xaml.dll "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" "/resource:$root\main.xaml,update.main.xaml" "/resource:$root\update.xaml,update.dialog.xaml" "/resource:$root\app.ico,update.icon" "/out:$output" (Join-Path $root 'src\Backend.cs') (Join-Path $root 'src\Updater.cs') (Join-Path $root 'src\UpdateDialog.cs') (Join-Path $PSScriptRoot 'UpdaterTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed to compile.' }
    if ($Live) { & $output $fixture '--live' } else { & $output $fixture }
    if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed.' }
} finally {
    if ([IO.Path]::GetFileName($directory).StartsWith('oppo-updater-tests-')) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
