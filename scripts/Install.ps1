<#
.SYNOPSIS
	Switches the HYTE Y70 Touch display from HYTE Nexus to Arka Hyte (or back with -Uninstall).

.DESCRIPTION
	Install:
		- disables the "HYTE Nexus" scheduled task and stops Nexus
		- registers the "Arka Hyte" logon task running Start-ArkaHyte.ps1
		- creates the "Arka Hyte" Start menu shortcut
	Uninstall reverts every step; Nexus itself is never uninstalled.

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

$nexusTaskName = 'HYTE Nexus'
$nexusProcesses = @('HYTE Nexus', 'HYTE.Nexus.Service')
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

	# Nexus off
	if (Get-ScheduledTask -TaskName $nexusTaskName -ErrorAction SilentlyContinue) {
		Disable-ScheduledTask -TaskName $nexusTaskName | Out-Null
		Write-Host "Scheduled task '$nexusTaskName' disabled"
	}
	else {
		Write-Warning "Scheduled task '$nexusTaskName' not found"
	}
	Get-Process -Name $nexusProcesses -ErrorAction SilentlyContinue | Stop-Process -Force
	Write-Host 'Nexus processes stopped'

	# Launcher at logon, in the user session (the display needs the interactive desktop)
	$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcherScript`""
	$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
	$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
	$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
	Register-ScheduledTask -TaskName $launcherTaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
	Write-Host "Scheduled task '$launcherTaskName' registered"

	New-StartMenuShortcut

	Start-ScheduledTask -TaskName $launcherTaskName
	Write-Host "Launcher started, logs in $(Join-Path $env:LOCALAPPDATA 'ArkaHyte')"
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

	if (Get-ScheduledTask -TaskName $nexusTaskName -ErrorAction SilentlyContinue) {
		Enable-ScheduledTask -TaskName $nexusTaskName | Out-Null
		Start-ScheduledTask -TaskName $nexusTaskName
		Write-Host "Scheduled task '$nexusTaskName' re-enabled and started"
	}
}

if ($Uninstall) {
	Uninstall-ArkaHyte
}
else {
	Install-ArkaHyte
}
