$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('oppo-preferences-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$oldData = $env:OPPO_MOUSE_SHARING_DATA
$oldExecutable = $env:OPPO_MOUSE_SHARING_EXE
$testRegistry = 'Software\OPPO-Mouse-sharing\Tests\' + [Guid]::NewGuid().ToString('N')
$checks = 0
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
try {
    $env:OPPO_MOUSE_SHARING_DATA = $testRoot
    $env:OPPO_MOUSE_SHARING_EXE = Join-Path $testRoot 'test backend.exe'
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $fixtureSource = Join-Path $testRoot 'BackendFixture.cs'
    [IO.File]::WriteAllText($fixtureSource, 'class Fixture { static void Main() { System.Threading.Thread.Sleep(60000); } }')
    & $compiler /nologo /target:winexe "/out:$env:OPPO_MOUSE_SHARING_EXE" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    . (Join-Path $root '界面.ps1') -ValidateOnly
    $script:startupRegistrySubKey = $testRegistry
    $script:startupTrayRequested = $true
    Complete-WindowStartup
    Assert ($window.WindowState -eq [Windows.WindowState]::Minimized) 'Startup did not minimize to the tray.'
    Show-MainWindow
    Complete-WindowStartup
    Assert ($window.WindowState -eq [Windows.WindowState]::Normal) 'Restoring a startup instance minimized it again.'
    $ValidateOnly = $false

    Assert (-not $script:autoShareOnConnect) 'Automatic sharing must default to off.'
    $script:warnedClientVersions = @('16.2.99')
    $AutoShareSwitch.IsChecked = $true
    $AutoShareSwitch.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
    Assert $script:autoShareOnConnect 'Automatic sharing checkbox did not invoke its saved preference handler.'
    Set-ClosePreference $true
    Choose-Direction 'left'
    $saved = Get-Content -LiteralPath $helperSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($saved.autoShareOnConnect -and $saved.closeToTray -and $saved.direction -eq 'left') 'Saving another preference lost automatic sharing.'
    Assert ($saved.warnedClientVersions -contains '16.2.99') 'Version notice preference was lost.'

    Update-AutoSharing 'disconnected'
    Assert (-not $script:helperProcess) 'Disconnected device must not start sharing.'
    $script:connectionProcess = [pscustomobject]@{ Id = 4242; HasExited = $false }
    function Write-TestConnection([int]$watcherPid, [int]$secondsOffset) {
        [IO.File]::WriteAllText($helperConnectionPath, (@{ phase = 'connected'; phone = 'Test device'; method = 'USB'; watcherPid = $watcherPid; updated = [DateTimeOffset]::UtcNow.AddSeconds($secondsOffset).ToString('o') } | ConvertTo-Json -Compress))
    }
    Write-TestConnection 1 0
    Update-Connection
    Assert (-not $script:helperProcess -and $script:connectionPhase -eq 'unavailable') 'Wrong watcher identity triggered automatic sharing.'
    Write-TestConnection 4242 -30
    Update-Connection
    Assert (-not $script:helperProcess) 'Expired device state triggered automatic sharing.'
    Write-TestConnection 4242 30
    Update-Connection
    Assert (-not $script:helperProcess) 'Future device state triggered automatic sharing.'
    Write-TestConnection 4242 0
    Update-Connection
    $first = $script:helperProcess
    $firstId = $first.Id
    Assert ($first -and -not $first.HasExited) 'Connection must start the real fixture process.'
    1..6 | ForEach-Object { Update-AutoSharing 'connected' }
    Assert ($script:helperProcess.Id -eq $first.Id) 'Repeated polls started another backend.'
    Toggle-Sharing
    Assert (Test-Path -LiteralPath $helperStopPath) 'Manual stop did not request backend shutdown.'
    $first.Kill(); $first.WaitForExit()
    $ShareButton.IsEnabled = $true
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess.Id -eq $first.Id -and $first.HasExited) 'Manual stop restarted within the same connection.'
    Update-AutoSharing 'unavailable'
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess.Id -eq $first.Id) 'Temporary watcher failure must not reset the connection attempt.'

    Update-AutoSharing 'disconnected'
    Update-AutoSharing 'connected'
    $second = $script:helperProcess
    Assert (-not $second.HasExited -and $second.Id -ne $firstId) 'Reconnection must start sharing again.'
    $second.Kill(); $second.WaitForExit()
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess.Id -eq $second.Id) 'Failed backend was retried continuously.'

    # 新连接到达时，旧进程尚未结束：等退出后启动，不能丢掉这次机会。
    $second.Dispose(); $script:helperProcess = $null
    $script:autoShareAttempted = $false
    Toggle-Sharing
    $third = $script:helperProcess
    $thirdId = $third.Id
    Update-AutoSharing 'disconnected'
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess.Id -eq $third.Id) 'Reconnection duplicated a still-running backend.'
    $third.Kill(); $third.WaitForExit()
    Update-AutoSharing 'connected'
    Assert (-not $script:helperProcess.HasExited -and $script:helperProcess.Id -ne $thirdId) 'Reconnect was lost while the old backend was exiting.'
    $active = $script:helperProcess
    Set-AutoSharePreference $false
    Assert (-not $active.HasExited) 'Disabling automatic sharing must leave the active session running.'
    $active.Kill(); $active.WaitForExit()
    Update-AutoSharing 'disconnected'
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess.HasExited) 'Disabled automatic sharing started a backend.'
    $active.Dispose(); $script:helperProcess = $null

    $script:autoShareOnConnect = $true
    $script:autoShareAttempted = $false
    $script:helperClosing = $true
    Update-AutoSharing 'connected'
    Assert (-not $script:helperProcess) 'Exiting application must not start sharing.'
    $script:helperClosing = $false
    $ValidateOnly = $true
    Update-AutoSharing 'connected'
    Assert (-not $script:helperProcess) 'Validation mode must not start sharing.'
    $ValidateOnly = $false

    $script:helperClosing = $true
    Set-AutoSharePreference $true
    $reloadCode = ". '" + (Join-Path $root '界面.ps1').Replace("'", "''") + "' -ValidateOnly; if (-not `$script:autoShareOnConnect -or `$AutoShareSwitch.IsChecked -ne `$true) { throw 'Saved automatic sharing preference was not loaded.' }"
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -Command $reloadCode
    Assert ($LASTEXITCODE -eq 0) 'Preferences did not survive a new UI process.'

    Assert (-not (Get-StartupEnabled)) 'Startup must initially be off in the isolated key.'
    $settingsView = New-SettingsDialog
    $startupControl = $settingsView.FindName('StartupSwitch')
    $startupControl.IsChecked = $true
    $startupControl.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
    Assert (Get-StartupEnabled) 'Startup registration did not persist.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistry, $true)
    try {
        Assert ($key.GetValue($script:startupRegistryName) -eq ('"' + $helperExecutable + '" --startup')) 'Startup executable path was not quoted correctly.'
        $key.SetValue('KeepOtherValue', 'untouched')
    } finally { $key.Close() }
    Set-StartupEnabled $false
    Assert (-not (Get-StartupEnabled)) 'Disabling startup did not remove the registration.'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testRegistry)
    try { Assert ($key.GetValue('KeepOtherValue') -eq 'untouched') 'Disabling startup changed another value.' }
    finally { $key.Close() }
    Write-Output ("Preferences and automatic sharing passed: {0} checks." -f $checks)
} finally {
    if ($script:helperProcess) {
        if (-not $script:helperProcess.HasExited) { $script:helperProcess.Kill(); $script:helperProcess.WaitForExit() }
        $script:helperProcess.Dispose()
    }
    $env:OPPO_MOUSE_SHARING_DATA = $oldData
    $env:OPPO_MOUSE_SHARING_EXE = $oldExecutable
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($testRegistry, $false)
    # 所有临时文件只在本次测试自己创建的目录中。
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTestRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedTestRoot -Leaf).StartsWith('oppo-preferences-')) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
