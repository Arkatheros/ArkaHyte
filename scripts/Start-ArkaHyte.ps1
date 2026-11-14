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

# One file per day next to the display's logs, the last week kept (as the display does)
$logDir = Join-Path $env:LOCALAPPDATA 'ArkaHyte\logs'
$logFile = Join-Path $logDir ('launcher-{0:yyyy-MM-dd}.log' -f (Get-Date))
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Get-ChildItem -Path $logDir -Filter 'launcher-*.log' -ErrorAction SilentlyContinue |
	Where-Object { $_.LastWriteTime -lt (Get-Date).Date.AddDays(-6) } |
	Remove-Item -ErrorAction SilentlyContinue

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
