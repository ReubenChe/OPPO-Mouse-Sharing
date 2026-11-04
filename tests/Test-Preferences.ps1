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
    Initialize-Tray
    $menuTexts = @($script:trayMenu.Items | ForEach-Object { $_.Text })
    Assert (($menuTexts -contains '打开 OPPO 互联屏幕协同设置') -and ($menuTexts -notcontains '手机在左侧') -and ($menuTexts -notcontains '手机在右侧')) 'Tray menu entries are incorrect.'
    Set-ClientMode 'sdk' '17.100.10'; Update-TrayMenu
    Assert $script:trayClientSharingItem.Enabled 'SDK tray entry must be enabled for the new client.'
    Assert ($ShareButton.Content -eq '开始协同' -and $script:trayToggleItem.Text -eq '开始协同') 'New client must use the start collaboration label.'
    $ShareButton.Content = Get-StopSharingLabel; Update-TrayMenu
    Assert ($ShareButton.Content -eq '结束协同' -and $script:trayToggleItem.Text -eq '结束协同') 'New tray sharing action must match the main button.'
    Assert ($script:trayExitItem.Text -eq '结束协同并退出') 'New tray exit label is incorrect.'
    $ShareButton.Content = '启用共享'
    Set-ClientMode 'legacy' '16.2.20'; Update-TrayMenu
    Assert (-not $script:trayClientSharingItem.Enabled) 'SDK tray entry must be disabled for the legacy client.'
    Assert ((Get-StopSharingLabel) -eq '停止共享' -and $script:trayExitItem.Text -eq '停止共享并退出') 'Legacy stop labels changed.'
    Assert ($ShareButton.Content -eq '启用共享' -and $script:trayToggleItem.Text -eq '启用共享') 'Legacy client must retain its start label.'
    Set-ClientMode 'sdk' '17.100.10'
    Assert ($DirectionControls.Visibility -eq 'Collapsed' -and $LegacyDiagram.Visibility -eq 'Collapsed' -and $SdkOverview.Visibility -eq 'Visible') 'New client must use the direction-free view.'
    Assert ($OpenClientSharingButton.Visibility -eq 'Visible') 'New client must expose the official collaboration page.'
    Set-ClientMode 'legacy' '16.2.20'
    Assert ($DirectionControls.Visibility -eq 'Visible' -and $LegacyDiagram.Visibility -eq 'Visible' -and $SdkOverview.Visibility -eq 'Collapsed') 'Legacy client direction controls must remain available.'
    Assert (-not $script:guideShown) 'First-run guide must be pending for a new user.'
    $script:projectOpened = $false
    function Open-ProjectHome { $script:projectOpened = $true }
    $guideDialog = New-SettingsDialog
    Assert ($guideDialog.FindName('HelpTab') -and $guideDialog.FindName('AmdCommand') -and $guideDialog.FindName('IntelCommand')) 'Help tab or processor instructions are missing.'
    Assert ($guideDialog.FindName('GithubButton')) 'GitHub button is missing.'
    $guideDialog.Content.Measure([Windows.Size]::new(584,611))
    $guideDialog.Content.Arrange([Windows.Rect]::new(0,0,584,611))
    $guideDialog.Content.UpdateLayout()
    $githubButton = $guideDialog.FindName('GithubButton')
    $githubSurface = $githubButton.Template.FindName('Surface', $githubButton)
    $githubSurface.BorderThickness = [Windows.Thickness]::new(2)
    $guideDialog.Content.UpdateLayout()
    Assert ($githubSurface.Child.ActualWidth -ge $githubButton.Content.Width -and $githubSurface.Child.ActualHeight -ge $githubButton.Content.Height) 'GitHub icon is clipped when the focus border is shown.'
    $guideDialog.FindName('GithubButton').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    Assert $script:projectOpened 'GitHub button did not invoke the project link handler.'
    Assert ((Resolve-ProcessorVendor @('AuthenticAMD')) -eq 'AMD') 'AMD processor was not recognized.'
    Assert ((Resolve-ProcessorVendor @('GenuineIntel')) -eq 'Intel') 'Intel processor was not recognized.'
    Assert ((Resolve-ProcessorVendor @('', 'Intel64 Family 6 GenuineIntel')) -eq 'Intel') 'Processor fallback was not recognized.'
    Assert ((Resolve-ProcessorVendor @('', 'unknown')) -eq '未能自动识别') 'Unknown processor must not be misidentified.'
    Assert ($guideDialog.FindName('CpuHint').Text -like ('*'+(Get-ProcessorVendor)+'*')) 'Help page did not display detected processor type.'
    $script:guideShown = $true
    $script:warnedClientVersions = @('16.2.99')
    $AutoShareSwitch.IsChecked = $true
    $AutoShareSwitch.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
    Assert $script:autoShareOnConnect 'Automatic sharing checkbox did not invoke its saved preference handler.'
    Set-ClosePreference $true
    Choose-Direction 'left'
    $saved = Get-Content -LiteralPath $helperSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($saved.autoShareOnConnect -and $saved.closeToTray -and $saved.direction -eq 'left') 'Saving another preference lost automatic sharing.'
    Assert ($saved.warnedClientVersions -contains '16.2.99') 'Version notice preference was lost.'
    Assert ($saved.guideShown -eq $true) 'First-run guide record was lost when saving preferences.'

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

    Set-ClientMode 'sdk' '17.100.10'
    $script:clientSharingActive = $false
    $script:autoShareOnConnect = $true; $script:autoShareAttempted = $false
    Update-AutoSharing 'connected'
    Assert ($script:helperProcess -and -not $script:helperProcess.HasExited) 'New client auto sharing did not start the backend.'
    $script:helperProcess.Kill(); $script:helperProcess.WaitForExit(); $script:helperProcess.Dispose(); $script:helperProcess = $null
    if (Test-Path $helperStopPath) { Remove-Item -LiteralPath $helperStopPath }
    Set-ClientMode 'legacy' '16.2.20'

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
    $reloadCode = ". '" + (Join-Path $root '界面.ps1').Replace("'", "''") + "' -ValidateOnly; if (-not `$script:autoShareOnConnect -or `$AutoShareSwitch.IsChecked -ne `$true -or -not `$script:guideShown) { throw 'Saved preferences were not loaded.' }"
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
    $helperExecutable = Join-Path $testRoot 'missing.exe'
    $failureView = New-SettingsDialog
    $failureSwitch = $failureView.FindName('StartupSwitch')
    $failureSwitch.IsChecked = $true
    $failureSwitch.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent))
    Assert ($failureSwitch.IsChecked -eq $false -and $failureView.FindName('SettingsFeedback').Text -like '无法更新开机自启动*') 'Startup failure was not shown and rolled back.'
    Write-Output ("Preferences and automatic sharing passed: {0} checks." -f $checks)
} finally {
    if ($script:trayIcon) { $script:trayIcon.Visible = $false; $script:trayIcon.Dispose() }
    if ($script:trayMenu) { $script:trayMenu.Dispose() }
    if ($script:trayDrawingIcon) { $script:trayDrawingIcon.Dispose() }
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
