<#
.SYNOPSIS
	Builds the Arka Hyte installer (Setup.exe) and update packages, and optionally publishes them
	as a GitHub release that installed copies pick up from their tray menu.

.DESCRIPTION
	1. dotnet publish with the given version
	2. downloads the previous GitHub release, so vpk can make a small delta package
	3. vpk pack: artifacts\releases\ArkaHyte.App-win-Setup.exe + update packages
	4. with -Publish: vpk upload to https://github.com/Arkatheros/ArkaHyte (tag v<version>)

	The repository must be public: installed copies read the releases without credentials.
	Publishing needs a GitHub token allowed to create releases (classic "repo" scope or a
	fine-grained token with "Contents: read and write"), in -Token or $env:GITHUB_TOKEN.

.EXAMPLE
	.\New-Release.ps1 -Version 1.0.0
	.\New-Release.ps1 -Version 1.0.1 -Publish
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[ValidatePattern('^\d+\.\d+\.\d+$')]
	[string]$Version,
	[switch]$Publish,
	[string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'

# Install folder is %LOCALAPPDATA%\<packId>: it must differ from the data folder
# %LOCALAPPDATA%\ArkaHyte (shortcuts, logs, settings), which uninstall would otherwise delete
$packId = 'ArkaHyte.App'
$repoUrl = 'https://github.com/Arkatheros/ArkaHyte'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ArkaHyte'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$releasesDir = Join-Path $repoRoot 'artifacts\releases'
$icon = Join-Path $project 'Assets\app.ico'

function Invoke-Native([string]$description, [scriptblock]$command) {
	Write-Host "== $description"
	& $command
	if ($LASTEXITCODE -ne 0) {
		throw "$description failed (exit code $LASTEXITCODE)"
	}
}

if ($Publish -and -not $Token) {
	throw 'Publishing needs a GitHub token: -Token or $env:GITHUB_TOKEN.'
}

Push-Location $repoRoot
try {
	if (Test-Path $publishDir) {
		Remove-Item -Recurse -Force $publishDir
	}
	Invoke-Native "Publish $Version" { dotnet publish $project -c Release -r win-x64 --self-contained false -o $publishDir "-p:Version=$Version" }
	Invoke-Native 'Restore vpk' { dotnet tool restore }

	# Previous release for the delta package; none yet on the very first release
	$downloadArgs = @('vpk', 'download', 'github', '--repoUrl', $repoUrl, '-o', $releasesDir)
	if ($Token) {
		$downloadArgs += @('--token', $Token)
	}
	Write-Host '== Download previous release'
	& dotnet @downloadArgs
	if ($LASTEXITCODE -ne 0) {
		Write-Warning 'No previous release downloaded: full package only'
	}

	Invoke-Native "Pack $Version" {
		dotnet vpk pack `
			--packId $packId `
			--packVersion $Version `
			--packDir $publishDir `
			--runtime 'win-x64' `
			--mainExe 'ArkaHyte.exe' `
			--packTitle 'Arka Hyte' `
			--packAuthors 'Arkatheros' `
			--icon $icon `
			--framework 'net10.0-x64-desktop' `
			--shortcuts 'StartMenuRoot' `
			--outputDir $releasesDir
	}

	if ($Publish) {
		Invoke-Native "Upload v$Version to GitHub" {
			dotnet vpk upload github `
				--repoUrl $repoUrl `
				--token $Token `
				--outputDir $releasesDir `
				--publish `
				--releaseName "Arka Hyte $Version" `
				--tag "v$Version"
		}
	}

	Write-Host "Installer: $(Join-Path $releasesDir "$packId-win-Setup.exe")"
}
finally {
	Pop-Location
}
