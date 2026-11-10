<#
.SYNOPSIS
	Builds the Arka Hyte installers and update packages, and optionally publishes them as a GitHub
	release that installed copies pick up from their tray menu.

.DESCRIPTION
	1. dotnet publish with the given version
	2. downloads the previous GitHub release, so vpk can make a small delta package
	3. vpk pack, then installers renamed in artifacts\releases:
		ArkaHyte-Setup.msi     wizard with confirmation (texts and images from installer\)
		ArkaHyte-Setup.exe     one-click install
		ArkaHyte-Portable.zip
	4. with -Publish: vpk upload to https://github.com/Arkatheros/ArkaHyte (tag v<version>)

	The repository must be public: installed copies read the releases without credentials.
	Publishing needs a GitHub token allowed to create releases (classic "public_repo" scope or a
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
# %LOCALAPPDATA%\ArkaHyte (shortcuts, logs, settings), which uninstall would otherwise delete.
# Never change it: installed copies only accept updates carrying the same id.
$packId = 'ArkaHyte.App'
$repoUrl = 'https://github.com/Arkatheros/ArkaHyte'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ArkaHyte'
$installerDir = Join-Path $repoRoot 'installer'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$releasesDir = Join-Path $repoRoot 'artifacts\releases'

# vpk names its files <packId>-win-*; the published names drop the technical id
$assetNames = [ordered]@{
	"$packId-win.msi" = 'ArkaHyte-Setup.msi'
	"$packId-win-Setup.exe" = 'ArkaHyte-Setup.exe'
	"$packId-win-Portable.zip" = 'ArkaHyte-Portable.zip'
}

function Invoke-Native([string]$description, [scriptblock]$command) {
	Write-Host "== $description"
	& $command
	if ($LASTEXITCODE -ne 0) {
		throw "$description failed (exit code $LASTEXITCODE)"
	}
}

# Renames the installers and the manifest entries vpk upload reads (assets.win.json); updates
# only use releases.win.json and the .nupkg, which keep their names
function Rename-Installers {
	$manifestPath = Join-Path $releasesDir 'assets.win.json'
	$manifest = Get-Content $manifestPath -Raw
	foreach ($name in $assetNames.Keys) {
		$source = Join-Path $releasesDir $name
		if (Test-Path $source) {
			Move-Item -LiteralPath $source -Destination (Join-Path $releasesDir $assetNames[$name]) -Force
			$manifest = $manifest.Replace("`"$name`"", "`"$($assetNames[$name])`"")
		}
	}
	[IO.File]::WriteAllText($manifestPath, $manifest)
}

if ($Publish -and -not $Token) {
	throw 'Publishing needs a GitHub token: -Token or $env:GITHUB_TOKEN.'
}

Push-Location $repoRoot
try {
	foreach ($dir in @($publishDir, $releasesDir)) {
		if (Test-Path $dir) {
			Remove-Item -Recurse -Force $dir
		}
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
			--icon (Join-Path $project 'Assets\app.ico') `
			--framework 'net10.0-x64-desktop' `
			--shortcuts 'StartMenuRoot' `
			--msi `
			--instLocation 'PerUser' `
			--instWelcome (Join-Path $installerDir 'welcome.txt') `
			--instConclusion (Join-Path $installerDir 'conclusion.txt') `
			--msiTopBanner (Join-Path $installerDir 'banner.bmp') `
			--msiDialogBackground (Join-Path $installerDir 'dialog.bmp') `
			--outputDir $releasesDir
	}
	Rename-Installers

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

	Write-Host "Installers: $releasesDir\ArkaHyte-Setup.msi, ArkaHyte-Setup.exe"
}
finally {
	Pop-Location
}
