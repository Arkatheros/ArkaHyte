<#
.SYNOPSIS
	Development install: starts the Arka Hyte build in bin\ArkaHyte at logon (removed with -Uninstall).

.DESCRIPTION
	Install:
		- registers the "Arka Hyte" logon task running Start-ArkaHyte.ps1
		- creates the "Arka Hyte" Start menu shortcut
	Uninstall reverts every step.
	HYTE Nexus is left alone: disable or uninstall it yourself, or both fight over the case display.
	Released versions use the installer instead (scripts\New-Release.ps1): do not combine both.

	Build the binaries first:
		dotnet publish src\ArkaHyte -c Release -o bin\ArkaHyte

.EXAMPLE
	.\Install.ps1
	.\Install.ps1 -Uninstall
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
	[switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$launcherTaskName = 'Arka Hyte'
$appProcess = 'ArkaHyte'

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherScript = Join-Path $PSScriptRoot 'Start-ArkaHyte.ps1'
$appExe = Join-Path $repoRoot 'bin\ArkaHyte\ArkaHyte.exe'
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$programsFolder = [Environment]::GetFolderPath('Programs')
$startMenuShortcut = Join-Path $programsFolder 'Arka Hyte.lnk'

# Start menu entry so the display can be relaunched from Windows search after "Quitter"
function New-StartMenuShortcut {
	$shell = New-Object -ComObject WScript.Shell
	try {
		$shortcut = $shell.CreateShortcut($startMenuShortcut)
		$shortcut.TargetPath = $appExe
		$shortcut.WorkingDirectory = Split-Path -Parent $appExe
		$shortcut.IconLocation = "$appExe,0"
		$shortcut.Description = 'Ecran du boitier Hyte Y70 Touch'
		$shortcut.Save()
		Write-Host "Start menu shortcut created: $startMenuShortcut"
	}
	finally {
		[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
	}
}

function Install-ArkaHyte {
	if (-not (Test-Path $appExe)) {
		throw "Arka Hyte is not built ($appExe). Run: dotnet publish src\ArkaHyte -c Release -o bin\ArkaHyte"
	}

	# Launcher at logon, in the user session (the display needs the interactive desktop)
	$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcherScript`""
	$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
	$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
	$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
	Register-ScheduledTask -TaskName $launcherTaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
	Write-Host "Scheduled task '$launcherTaskName' registered"

	New-StartMenuShortcut

	Start-ScheduledTask -TaskName $launcherTaskName
	Write-Host "Launcher started, logs in $(Join-Path $env:LOCALAPPDATA 'ArkaHyte\logs')"
}

function Uninstall-ArkaHyte {
	if (Get-ScheduledTask -TaskName $launcherTaskName -ErrorAction SilentlyContinue) {
		Unregister-ScheduledTask -TaskName $launcherTaskName -Confirm:$false
		Write-Host "Scheduled task '$launcherTaskName' removed"
	}
	Get-Process -Name $appProcess -ErrorAction SilentlyContinue | Stop-Process -Force
	Write-Host 'Arka Hyte stopped'
	if (Test-Path $startMenuShortcut) {
		Remove-Item -LiteralPath $startMenuShortcut
		Write-Host "Start menu shortcut removed: $startMenuShortcut"
	}
}

if ($Uninstall) {
	Uninstall-ArkaHyte
}
else {
	Install-ArkaHyte
}
