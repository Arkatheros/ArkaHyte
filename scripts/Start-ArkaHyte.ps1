<#
.SYNOPSIS
	Starts Arka Hyte (ArkaHyte.exe) on the HYTE Y70 case display.

.DESCRIPTION
	ArkaHyte finds the case screen by its native resolution and places itself, and includes
	the touch cursor guard, so this launcher only makes sure it is running. Meant to run at logon
	from the scheduled task registered by Install.ps1.
#>
[CmdletBinding()]
param(
	# Resolved below: Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults
	[string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
	$ExePath = Join-Path $repoRoot 'bin\ArkaHyte\ArkaHyte.exe'
}

$logDir = Join-Path $env:LOCALAPPDATA 'ArkaHyte'
$logFile = Join-Path $logDir 'launcher.log'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-Log([string]$message) {
	$line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $message
	Add-Content -Path $logFile -Value $line
	Write-Verbose $line
}

# Any failure, even outside the main block, still reaches the log
trap {
	Write-Log "Fatal: $($_.Exception.Message)"
	exit 1
}

Write-Log '--- Launcher started'

# Programs of earlier versions (before the rename and the built-in touch guard)
Get-Process -Name 'HyteDisplay', 'HyteTouchGuard' -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not (Test-Path $ExePath)) {
	Write-Log "Warning: ArkaHyte not found at '$ExePath'"
}
elseif (Get-Process -Name 'ArkaHyte' -ErrorAction SilentlyContinue) {
	Write-Log 'ArkaHyte already running'
}
else {
	Start-Process -FilePath $ExePath
	Write-Log 'ArkaHyte started'
}
