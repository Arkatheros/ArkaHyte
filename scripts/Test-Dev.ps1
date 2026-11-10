<#
.SYNOPSIS
	Runs the development build in place of the installed Arka Hyte, then gives the case display back
	to the installed copy: test a change without publishing a release.

.DESCRIPTION
	1. publishes the working tree to bin\ArkaHyte (skipped with -NoBuild)
	2. stops every running Arka Hyte (installed or development: single instance)
	3. starts bin\ArkaHyte\ArkaHyte.exe with -Arguments and waits while it runs; a restart by the
	   setup wizard is followed (the new process has the same path)
	4. when it is closed (tray menu > Quitter) or on Ctrl+C here, stops it and restarts the
	   installed copy (%LOCALAPPDATA%\ArkaHyte.App)

	Both copies share the data folder %LOCALAPPDATA%\ArkaHyte: settings, shortcuts and logs changed
	while testing stay for the installed copy. The development build shows "dev" next to its version
	in the tray menu and never looks for updates.

.EXAMPLE
	.\Test-Dev.ps1
	.\Test-Dev.ps1 -Arguments '--setup'
	.\Test-Dev.ps1 -Arguments '--demo', 'stress' -NoBuild
#>
[CmdletBinding()]
param(
	[string[]]$Arguments = @(),
	[switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$devExe = Join-Path $repoRoot 'bin\ArkaHyte\ArkaHyte.exe'
$installedExe = Join-Path $env:LOCALAPPDATA 'ArkaHyte.App\current\ArkaHyte.exe'

function Stop-ArkaHyte {
	$running = @(Get-Process -Name ArkaHyte -ErrorAction SilentlyContinue)
	if ($running.Count -gt 0) {
		$running | Stop-Process -Force
		$running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
	}
}

function Get-DevProcess {
	Get-Process -Name ArkaHyte -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $devExe }
}

# Stopped first: a running copy from bin\ArkaHyte would lock the files being published
Write-Host '== Stop Arka Hyte'
Stop-ArkaHyte

if (-not $NoBuild) {
	Write-Host '== Publish the development build'
	dotnet publish (Join-Path $repoRoot 'src\ArkaHyte') -c Release -o (Join-Path $repoRoot 'bin\ArkaHyte') -nologo -v quiet
	if ($LASTEXITCODE -ne 0) {
		Write-Warning 'Build failed: restarting the installed copy'
		if (Test-Path $installedExe) {
			Start-Process $installedExe
		}
		exit 1
	}
}

try {
	Write-Host "== Development build running ($($Arguments -join ' ')). Tray menu > Quitter or Ctrl+C here to go back."
	if ($Arguments.Count -gt 0) {
		Start-Process $devExe -ArgumentList $Arguments
	}
	else {
		Start-Process $devExe
	}
	# Polling rather than Wait-Process: the setup wizard restarts the display as a new process
	Start-Sleep -Seconds 3
	while (Get-DevProcess) {
		Start-Sleep -Seconds 1
	}
}
finally {
	# Also runs on Ctrl+C
	Stop-ArkaHyte
	if (Test-Path $installedExe) {
		Write-Host '== Installed copy restarted'
		Start-Process $installedExe
	}
	else {
		Write-Host '== No installed copy to restart'
	}
}
