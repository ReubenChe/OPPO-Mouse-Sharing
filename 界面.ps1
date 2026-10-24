param([switch]$ValidateOnly, [switch]$StartSharing)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$helperRoot = $PSScriptRoot
$helperDataRoot = if ($env:OPPO_MOUSE_SHARING_DATA) { $env:OPPO_MOUSE_SHARING_DATA } else { $helperRoot }
[IO.Directory]::CreateDirectory($helperDataRoot) | Out-Null
$helperStatePath = Join-Path $helperDataRoot 'state.json'
$helperStopPath = Join-Path $helperDataRoot 'stop.request'
$helperSettingsPath = Join-Path $helperDataRoot 'settings.json'
$helperConnectionPath = Join-Path $helperDataRoot 'connection.json'
$helperExecutable = if ($env:OPPO_MOUSE_SHARING_EXE) { $env:OPPO_MOUSE_SHARING_EXE } else { Join-Path $helperRoot 'OPPO-Mouse-sharing.exe' }
$script:backendMode = '--backend'
$script:connectionProcess = $null
$script:helperProcess = $null
$script:helperClosing = $false
$script:selectedDirection = 'right'
$script:lastStateUpdate = ''
$script:startupShareRequested = [bool]$StartSharing
$script:closeToTray = $false
$script:warnedClientVersions = @()
$script:trayIcon = $null
$script:trayMenu = $null
$script:trayDrawingIcon = $null
$helperMutex = $null
if (-not $ValidateOnly) {
    $helperMutex = New-Object System.Threading.Mutex($false, 'Local\OppoConnectKmsHelper16220')
    if (-not $helperMutex.WaitOne(0, $false)) {
        [Windows.MessageBox]::Show('助手已经打开，请使用现有窗口。', '键鼠共享助手') | Out-Null
        exit
    }
}
try {
    $saved = Get-Content -LiteralPath $helperSettingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
    if ($saved.direction -in @('left','right')) { $script:selectedDirection = $saved.direction }
    if ($saved.closeToTray -is [bool]) { $script:closeToTray = $saved.closeToTray }
    $script:warnedClientVersions = @($saved.warnedClientVersions | Where-Object { $_ -is [string] -and -not [string]::IsNullOrWhiteSpace($_) })
} catch {}
[xml]$xaml = [IO.File]::ReadAllText((Join-Path $helperRoot 'main.xaml'))
$reader = New-Object System.Xml.XmlNodeReader($xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)
if (Test-Path -LiteralPath (Join-Path $helperRoot 'app.ico')) {
    $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create([Uri]::new((Join-Path $helperRoot 'app.ico')), [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $script:brandSource = $decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
    $window.Icon = $script:brandSource
    $window.FindName('BrandMark').Source = $script:brandSource
}
foreach ($name in @('StatusDot','StatusTitle','StatusDetail','ConnectionDot','ConnectionTitle','ConnectionDetail','DeviceCard','DeviceName','DeviceMethod','HeaderStatus','DeviceLayout','LeftButton','RightButton','PhoneFrame','PhoneCaption','Connector','ShareButton','DirectionHint')) {
    Set-Variable -Name $name -Value $window.FindName($name)
}
function Brush([string]$color) { return [Windows.Media.BrushConverter]::new().ConvertFromString($color) }
function Show-MainWindow {
    $window.ShowInTaskbar = $true
    $window.WindowState = [Windows.WindowState]::Normal
    if (-not $ValidateOnly) { $window.Show(); $window.Activate() | Out-Null }
}
function Minimize-ToTray {
    $window.WindowState = [Windows.WindowState]::Minimized
    if ($script:trayIcon) { $window.ShowInTaskbar = $false; $window.Hide() }
}
function Update-TrayMenu {
    if (-not $script:trayIcon) { return }
    $running = $script:helperProcess -and -not $script:helperProcess.HasExited
    $script:trayToggleItem.Text = $(if ($running) { '停止共享' } else { '启用共享' })
    $script:trayToggleItem.Enabled = $ShareButton.IsEnabled -and -not $script:helperClosing
    $script:trayLeftItem.Checked = $script:selectedDirection -eq 'left'
    $script:trayRightItem.Checked = $script:selectedDirection -eq 'right'
    $script:trayLeftItem.Enabled = -not $script:helperClosing
    $script:trayRightItem.Enabled = -not $script:helperClosing
    $script:trayRememberItem.Checked = $script:closeToTray
    $script:trayStatusItem.Text = '共享状态：' + $StatusTitle.Text
    $script:trayIcon.Text = '键鼠共享助手 - ' + $StatusTitle.Text
}
function Request-ApplicationExit {
    if ($script:helperClosing) { return }
    try {
        $closing = [ComponentModel.CancelEventArgs]::new()
        Apply-CloseChoice 'exit' $closing
        if (-not $closing.Cancel) { $script:helperClosing = $true; $window.Close() }
        Update-TrayMenu
    } catch { Show-HelperError '退出操作失败' $_ }
}
function Initialize-Tray {
    $script:trayMenu = [Windows.Forms.ContextMenuStrip]::new()
    $script:trayMenu.Font = [Drawing.Font]::new('Microsoft YaHei UI', 9)
    $open = [Windows.Forms.ToolStripMenuItem]::new('显示主窗口')
    $script:trayStatusItem = [Windows.Forms.ToolStripMenuItem]::new('共享状态：尚未启用')
    $script:trayStatusItem.Enabled = $false
    $script:trayToggleItem = [Windows.Forms.ToolStripMenuItem]::new('启用共享')
    $script:trayLeftItem = [Windows.Forms.ToolStripMenuItem]::new('手机在左侧')
    $script:trayRightItem = [Windows.Forms.ToolStripMenuItem]::new('手机在右侧')
    $script:trayRememberItem = [Windows.Forms.ToolStripMenuItem]::new('关闭时直接最小化到托盘')
    $script:trayExitItem = [Windows.Forms.ToolStripMenuItem]::new('停止共享并退出')
    $script:trayMenu.Items.AddRange([Windows.Forms.ToolStripItem[]]@(
        $open, $script:trayStatusItem, [Windows.Forms.ToolStripSeparator]::new(),
        $script:trayToggleItem, $script:trayLeftItem, $script:trayRightItem,
        [Windows.Forms.ToolStripSeparator]::new(), $script:trayRememberItem,
        [Windows.Forms.ToolStripSeparator]::new(), $script:trayExitItem
    ))
    $script:trayDrawingIcon = [Drawing.Icon]::new((Join-Path $helperRoot 'app.ico'))
    $script:trayIcon = [Windows.Forms.NotifyIcon]::new()
    $script:trayIcon.Icon = $script:trayDrawingIcon
    $script:trayIcon.ContextMenuStrip = $script:trayMenu
    $open.Add_Click({ Show-MainWindow })
    $script:trayIcon.Add_DoubleClick({ Show-MainWindow })
    $script:trayMenu.Add_Opening({ Update-TrayMenu })
    $script:trayToggleItem.Add_Click({ if ($ShareButton.IsEnabled -and -not $script:helperClosing) { Toggle-Sharing; Update-TrayMenu } })
    $script:trayLeftItem.Add_Click({ Choose-Direction 'left' })
    $script:trayRightItem.Add_Click({ Choose-Direction 'right' })
    $script:trayRememberItem.Add_Click({
        try { Set-ClosePreference (-not $script:closeToTray) }
        catch { Show-HelperError '无法保存关闭选项' $_ }
    })
    $script:trayExitItem.Add_Click({ Request-ApplicationExit })
    Update-TrayMenu
    $script:trayIcon.Visible = -not $ValidateOnly
}
$window.Add_StateChanged({
    if ($window.WindowState -eq [Windows.WindowState]::Minimized -and $script:trayIcon -and -not $script:helperClosing) {
        $window.ShowInTaskbar = $false; $window.Hide()
    }
})
function Set-ConnectionDisplay([string]$phase, [string]$phone, [string]$method = '连接方式未知') {
    $DeviceCard.Background = Brush '#F5F5F5'; $DeviceCard.BorderBrush = Brush '#E4E4E4'
    $DeviceName.Foreground = Brush '#85888D'; $DeviceName.ToolTip = $null
    switch ($phase) {
        'connected' {
            $ConnectionDot.Fill = Brush '#27A948'; $ConnectionTitle.Text = '手机已连接'
            $ConnectionDetail.Text = '互联通道已建立。'; $ConnectionDetail.ToolTip = $null
            $DeviceCard.Background = Brush '#F0F5FF'; $DeviceCard.BorderBrush = Brush '#CCDDFA'
            $DeviceName.Foreground = Brush '#0066E6'; $DeviceName.Text = $phone; $DeviceName.ToolTip = $phone
            $DeviceMethod.Text = $(if ($method) { $method } else { '连接方式未知' })
        }
        'disconnected' {
            $ConnectionDot.Fill = Brush '#93979D'; $ConnectionTitle.Text = '手机未连接'
            $ConnectionDetail.Text = '请在 OPPO 互联中连接手机。'; $ConnectionDetail.ToolTip = $null
            $DeviceName.Text = '未连接手机'; $DeviceMethod.Text = '等待手机连接'
        }
        default {
            $ConnectionDot.Fill = Brush '#B87918'; $ConnectionTitle.Text = '连接未确认'
            $ConnectionDetail.Text = '请确认 OPPO 互联正在运行。'; $ConnectionDetail.ToolTip = $null
            $DeviceName.Text = '未确认设备'; $DeviceMethod.Text = '暂未获取连接信息'
        }
    }
}
function Start-ConnectionWatcher {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $helperExecutable
    $info.Arguments = '--connection-watch ' + $PID
    $info.WorkingDirectory = $helperRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $script:connectionProcess = [Diagnostics.Process]::Start($info)
}
function Update-Connection {
    try {
        if (-not $script:connectionProcess -or $script:connectionProcess.HasExited) { throw 'Watcher unavailable' }
        $connection = Get-Content -LiteralPath $helperConnectionPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($connection.updated)).TotalSeconds
        if ($connection.watcherPid -ne $script:connectionProcess.Id -or $age -gt 7 -or $age -lt -2) { throw 'Connection state expired' }
        Set-ConnectionDisplay $connection.phase $connection.phone $connection.method
    } catch { Set-ConnectionDisplay 'unavailable' '' }
}
function Save-Settings([string]$direction, [bool]$closeToTray) {
    $temporary = $helperSettingsPath + '.tmp'
    $backup = $helperSettingsPath + '.bak'
    [IO.File]::WriteAllText($temporary, (@{ direction = $direction; closeToTray = $closeToTray; warnedClientVersions = @($script:warnedClientVersions) } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($helperSettingsPath)) {
        [IO.File]::Replace($temporary, $helperSettingsPath, $backup)
        try { [IO.File]::Delete($backup) } catch {}
    }
    else { [IO.File]::Move($temporary, $helperSettingsPath) }
}
function Save-Direction([string]$direction) { Save-Settings $direction $script:closeToTray }
function Show-CompatibilityMessage([string]$message) {
    if (-not $ValidateOnly) {
        Show-MainWindow
        [Windows.MessageBox]::Show($window, $message, '客户端兼容性提示', [Windows.MessageBoxButton]::OK, [Windows.MessageBoxImage]::Information) | Out-Null
    }
}
function Show-ClientVersionNotice([string]$version, [bool]$verified) {
    if ($verified -or [string]::IsNullOrWhiteSpace($version) -or $version -in $script:warnedClientVersions) { return }
    # 弹出模态对话框前先记录，避免消息循环处理定时器事件时重复提醒。
    $script:warnedClientVersions += $version
    try { Save-Settings $script:selectedDirection $script:closeToTray }
    catch {
        # 提醒记录保存失败时，也不能中止共享或在本次运行中重复弹窗。
        try { [IO.File]::AppendAllText((Join-Path $helperDataRoot 'ui-error.log'), ([DateTime]::Now.ToString('s') + ' 无法保存版本提醒记录：' + $_.Exception.Message + [Environment]::NewLine)) } catch {}
    }
    Show-CompatibilityMessage ('当前 OPPO 互联客户端版本为 ' + $version + '，适配性尚未确认。' + [Environment]::NewLine + [Environment]::NewLine + '如遇到问题，请联系开发者或到项目仓库提交 Issue。' + [Environment]::NewLine + [Environment]::NewLine + '此提示不会阻止启用共享，同一客户端版本仅提醒一次。')
}
function Set-ClosePreference([bool]$closeToTray) {
    Save-Settings $script:selectedDirection $closeToTray
    $script:closeToTray = $closeToTray
    Update-TrayMenu
}
function Show-HelperError([string]$message, $errorRecord) {
    $script:lastUiError = $message + ': ' + $errorRecord.Exception.Message
    try { [IO.File]::AppendAllText((Join-Path $helperDataRoot 'ui-error.log'), ([DateTime]::Now.ToString('s') + ' ' + $script:lastUiError + [Environment]::NewLine)) } catch {}
    $StatusDot.Fill = Brush '#BC3D3D'
    $StatusTitle.Text = $message
    $StatusDetail.Text = $errorRecord.Exception.Message
    if (-not $ValidateOnly) {
        Show-MainWindow
        [Windows.MessageBox]::Show($window, $message + [Environment]::NewLine + $errorRecord.Exception.Message, '键鼠共享助手') | Out-Null
    }
}
function Update-Direction([string]$direction) {
    $script:selectedDirection = $direction
    $left = $direction -eq 'left'
    $DeviceLayout.RenderTransform = [Windows.Media.TranslateTransform]::new($(if ($left) { 60 } else { -60 }), 0)
    $LeftButton.Background = Brush $(if ($left) { '#FFFFFF' } else { '#FFFFFF' })
    $LeftButton.BorderBrush = Brush $(if ($left) { '#0066FF' } else { '#E4E4E4' })
    $LeftButton.Foreground = Brush $(if ($left) { '#0066E6' } else { '#666A70' })
    $RightButton.Background = Brush $(if (-not $left) { '#FFFFFF' } else { '#FFFFFF' })
    $RightButton.BorderBrush = Brush $(if (-not $left) { '#0066FF' } else { '#E4E4E4' })
    $RightButton.Foreground = Brush $(if (-not $left) { '#0066E6' } else { '#666A70' })
    [Windows.Controls.Canvas]::SetLeft($PhoneFrame, $(if ($left) { 53 } else { 411 }))
    [Windows.Controls.Canvas]::SetLeft($PhoneCaption, $(if ($left) { 44 } else { 402 }))
    $Connector.Data = [Windows.Media.Geometry]::Parse($(if ($left) { 'M 160,76 L 121,76 M 127,70 L 121,76 L 127,82 M 121,91 L 160,91 M 154,85 L 160,91 L 154,97' } else { 'M 356,76 L 395,76 M 389,70 L 395,76 L 389,82 M 395,91 L 356,91 M 362,85 L 356,91 L 362,97' }))
    $DirectionHint.Text = $(if ($left) { '从电脑左侧进入手机 · 从手机右侧返回电脑' } else { '从电脑右侧进入手机 · 从手机左侧返回电脑' })
    Update-TrayMenu
}
function Choose-Direction([string]$direction) {
    try {
        Save-Direction $direction
        Update-Direction $direction
        if ($script:helperProcess -and -not $script:helperProcess.HasExited) {
            $StatusTitle.Text = '正在切换方向…'
            $StatusDetail.Text = '切换前请先将指针移回电脑。'
        }
    } catch { Show-HelperError '无法保存方向设置' $_ }
}
Update-Direction $script:selectedDirection
$LeftButton.Add_Click({ Choose-Direction 'left' })
$RightButton.Add_Click({ Choose-Direction 'right' })
function Toggle-Sharing {
    try {
    if ($script:helperProcess -and -not $script:helperProcess.HasExited) {
        [IO.File]::WriteAllText($helperStopPath, 'stop')
        $ShareButton.IsEnabled = $false
        $StatusTitle.Text = '正在停止共享…'
        return
    }
    if (-not (Test-Path -LiteralPath $helperExecutable)) {
        [Windows.MessageBox]::Show('没有找到 C# 后端程序：' + $helperExecutable, '无法启动') | Out-Null
        return
    }
    Save-Direction $script:selectedDirection
    if (Test-Path -LiteralPath $helperStopPath) { Remove-Item -LiteralPath $helperStopPath }
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $helperExecutable
    $startInfo.Arguments = $script:backendMode + ' ' + $PID
    $startInfo.WorkingDirectory = $helperRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $script:helperProcess = [Diagnostics.Process]::Start($startInfo)
    $StatusTitle.Text = '正在连接…'; $StatusDetail.Text = '请保持手机解锁。'
    $ShareButton.Content = '停止共享'; $script:lastStateUpdate = ''
    } catch { Show-HelperError '共享操作失败' $_ }
}
$ShareButton.Add_Click({ Toggle-Sharing })
$window.Add_ContentRendered({
    if ($script:startupShareRequested) {
        $script:startupShareRequested = $false
        $ShareButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    }
})
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(350)
$timer.Add_Tick({
    Update-Connection
    Update-TrayMenu
    if (-not $script:helperProcess) { return }
    if (Test-Path -LiteralPath $helperStatePath) {
        try {
            $current = Get-Content -LiteralPath $helperStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($current.backendPid -eq $script:helperProcess.Id -and $current.updated -ne $script:lastStateUpdate) {
                $script:lastStateUpdate = $current.updated
                switch ($current.phase) {
                    'armed' {
                        $StatusDot.Fill = Brush '#0066FF'; $StatusTitle.Text = '等待穿越'
                        $StatusDetail.Text = '靠近所选屏幕边缘即可建连。'
                        $ShareButton.Background = Brush '#30343B'; $ShareButton.BorderBrush = Brush '#30343B'
                    }
                    'ready' {
                        $StatusDot.Fill = Brush '#27A948'; $StatusTitle.Text = '正在共享'
                        $StatusDetail.Text = '从所选电脑边缘进入手机。'
                        $ShareButton.Background = Brush '#30343B'; $ShareButton.BorderBrush = Brush '#30343B'
                    }
                    'changing' { $StatusDot.Fill = Brush '#B87918'; $StatusTitle.Text = '正在切换方向…'; $StatusDetail.Text = '请先将指针移回电脑。' }
                    'starting' { $StatusDot.Fill = Brush '#0066FF'; $StatusTitle.Text = '正在连接…'; $StatusDetail.Text = '请保持手机解锁。' }
                    'stopping' { $StatusDot.Fill = Brush '#93979D'; $StatusTitle.Text = '正在停止共享…' }
                    'error' { $StatusDot.Fill = Brush '#BC3D3D'; $StatusTitle.Text = '共享已停止'; $StatusDetail.Text = $current.message }
                    'stopped' { $StatusDot.Fill = Brush '#93979D'; $StatusTitle.Text = '共享已停止'; $StatusDetail.Text = '选择手机所在方向，即可再次启用。' }
                }
                Show-ClientVersionNotice $current.clientVersion ($current.clientVersionVerified -eq $true)
            }
        } catch {}
    }
    if ($script:helperProcess.HasExited) {
        $ShareButton.Content = '启用共享'; $ShareButton.IsEnabled = $true
        $ShareButton.Background = Brush '#0066FF'; $ShareButton.BorderBrush = Brush '#0066FF'
        $script:helperProcess.Dispose(); $script:helperProcess = $null
        if ($script:helperClosing) { $window.Close() }
    }
})
function New-CloseDialog {
    [xml]$dialogXaml = [IO.File]::ReadAllText((Join-Path $helperRoot 'close.xaml'))
    $resources = $dialogXaml.CreateElement('Window.Resources', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    foreach ($style in $xaml.DocumentElement.SelectNodes('*[local-name()="Window.Resources"]/*')) {
        $resources.AppendChild($dialogXaml.ImportNode($style, $true)) | Out-Null
    }
    $dialogXaml.DocumentElement.PrependChild($resources) | Out-Null
    $dialogReader = New-Object System.Xml.XmlNodeReader($dialogXaml)
    $dialog = [Windows.Markup.XamlReader]::Load($dialogReader)
    $dialog.FindName('DialogBrandMark').Source = $script:brandSource
    $dialog.Tag = [pscustomobject]@{ Choice = 'cancel'; Remember = $false }
    if (-not $script:helperProcess -or $script:helperProcess.HasExited) {
        $dialog.FindName('CloseDescription').Text = '收起后可从右下角托盘图标恢复窗口或快捷操作。'
        $dialog.FindName('ExitChoice').Content = '退出应用'
    }
    $dialog.FindName('MinimizeChoice').Add_Click({ $dialog.Tag.Choice = 'minimize'; $dialog.Tag.Remember = $dialog.FindName('RememberMinimize').IsChecked -eq $true; $dialog.Close() }.GetNewClosure())
    $dialog.FindName('ExitChoice').Add_Click({ $dialog.Tag.Choice = 'exit'; $dialog.Tag.Remember = $dialog.FindName('RememberMinimize').IsChecked -eq $true; $dialog.Close() }.GetNewClosure())
    $dialog.FindName('CloseX').Add_Click({ $dialog.Tag.Choice = 'cancel'; $dialog.Close() }.GetNewClosure())
    return $dialog
}
function Apply-CloseChoice([string]$choice, $eventArgs) {
    $eventArgs.Cancel = $true
    switch ($choice) {
        'minimize' { Minimize-ToTray }
        'exit' {
            if ($script:helperProcess -and -not $script:helperProcess.HasExited) {
                [IO.File]::WriteAllText($helperStopPath, 'stop')
                $script:helperClosing = $true
                $ShareButton.IsEnabled = $false; $StatusTitle.Text = '正在停止共享并关闭…'
            } else { $eventArgs.Cancel = $false }
        }
    }
}
function Apply-DialogResult($result, $eventArgs) {
    if ($result.Choice -ne 'cancel') { Set-ClosePreference $result.Remember }
    Apply-CloseChoice $result.Choice $eventArgs
}
function Handle-WindowClosing($eventArgs) {
    if ($script:helperClosing) {
        $eventArgs.Cancel = [bool]($script:helperProcess -and -not $script:helperProcess.HasExited)
        return
    }
    $eventArgs.Cancel = $true
    if ($script:closeToTray) { Apply-CloseChoice 'minimize' $eventArgs; return }
    if ($script:closeDialogVisible) { return }
    try {
        $script:closeDialogVisible = $true
        $dialog = New-CloseDialog
        $dialog.Owner = $window
        $dialog.ShowDialog() | Out-Null
        Apply-DialogResult $dialog.Tag $eventArgs
    } catch { Show-HelperError '关闭操作失败' $_ }
    finally { $script:closeDialogVisible = $false }
}
$window.Add_Closing({ param($sender, $eventArgs) Handle-WindowClosing $eventArgs })
try {
    if ($ValidateOnly) {
        if (-not $LeftButton -or -not $RightButton -or -not $ShareButton) { throw 'Missing controls' }
        Update-Direction 'left'
        if ([Windows.Controls.Canvas]::GetLeft($PhoneFrame) -ne 53) { throw 'Left layout invalid' }
        Update-Direction 'right'
        if ([Windows.Controls.Canvas]::GetLeft($PhoneFrame) -ne 411) { throw 'Right layout invalid' }
        Write-Output 'WPF layout and direction validation passed'
    } else {
        Initialize-Tray
        try { Start-ConnectionWatcher } catch { Set-ConnectionDisplay 'unavailable' '' }
        # 调用 Hide() 会结束模态窗口的消息循环。主窗口隐藏到系统托盘时，
        # 仍需让应用的消息循环继续运行。
        $application = [Windows.Application]::new()
        $application.ShutdownMode = [Windows.ShutdownMode]::OnMainWindowClose
        $timer.Start()
        $application.Run($window) | Out-Null
    }
} finally {
    $timer.Stop()
    if ($script:trayIcon) { $script:trayIcon.Visible = $false; $script:trayIcon.Dispose() }
    if ($script:trayMenu) { $script:trayMenu.Dispose() }
    if ($script:trayDrawingIcon) { $script:trayDrawingIcon.Dispose() }
    if ($script:connectionProcess) {
        if (-not $script:connectionProcess.HasExited) { $script:connectionProcess.Kill() }
        $script:connectionProcess.Dispose()
    }
    if ($helperMutex) { $helperMutex.ReleaseMutex(); $helperMutex.Dispose() }
}
