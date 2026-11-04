$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$oldData = $env:OPPO_MOUSE_SHARING_DATA
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('oppo-sdk-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testDir) | Out-Null
$env:OPPO_MOUSE_SHARING_DATA = $testDir
$job = $null
try {
    . (Join-Path $root '界面.ps1') -ValidateOnly
    $script:clientSdkPipeName = 'oppo-test-' + [Guid]::NewGuid().ToString('N')
    $job = Start-Job -ArgumentList $script:clientSdkPipeName -ScriptBlock {
        param($name)
        foreach ($step in 1..2) {
            $server = [IO.Pipes.NamedPipeServerStream]::new($name, [IO.Pipes.PipeDirection]::InOut)
            try {
                $server.WaitForConnection()
                $reader = [IO.StreamReader]::new($server)
                $writer = [IO.StreamWriter]::new($server, [Text.UTF8Encoding]::new($false), 1024, $true)
                $writer.AutoFlush = $true
                $request = $reader.ReadLine() | ConvertFrom-Json
                if ($step -eq 1 -and $request.method -eq 'get_device_list') {
                    $data = @(@{ device_id = 'fixture'; device_type = 8; is_connected = $true })
                } elseif ($step -eq 2 -and $request.method -eq 'open_page' -and $request.params.page_type -eq 'screen_share_settings' -and $request.params.device_id -eq 'fixture') {
                    $data = @{ page_type = 'screen_share_settings' }
                } else { throw 'Unexpected request' }
                $writer.WriteLine((@{ success = $true; request_id = $request.request_id; data = $data } | ConvertTo-Json -Compress -Depth 8))
            } finally { $server.Dispose() }
        }
    }
    Open-ClientSharingPage
    $job | Wait-Job -Timeout 5 | Out-Null
    if ($job.State -ne 'Completed') { throw 'SDK fixture did not finish.' }
    Receive-Job $job -ErrorAction Stop
    try { Open-ClientSharingPage; throw 'Unavailable SDK was accepted.' }
    catch { if ($_.Exception.Message -notlike '*无法连接 OPPO 互联的 SDK*') { throw } }
    'SDK page request and unavailable-pipe handling passed.'
} finally {
    if ($job) { $job | Remove-Job -Force -ErrorAction SilentlyContinue }
    $env:OPPO_MOUSE_SHARING_DATA = $oldData
    if ((Split-Path $testDir -Leaf).StartsWith('oppo-sdk-test-')) { Remove-Item -LiteralPath $testDir -Recurse -Force }
}
