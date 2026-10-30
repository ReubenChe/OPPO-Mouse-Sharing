param([switch]$ValidateOnly, [switch]$StartSharing, [switch]$StartInTray)
& (Join-Path $PSScriptRoot '界面.ps1') @PSBoundParameters
