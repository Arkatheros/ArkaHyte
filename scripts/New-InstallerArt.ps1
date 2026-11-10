<#
.SYNOPSIS
	Draws the Sith-themed images of the MSI installer wizard into installer\.

.DESCRIPTION
	banner.bmp  493x58   top strip of the inner dialogs (title text drawn by Windows on the left)
	dialog.bmp  493x312  welcome / finish background (text drawn by Windows on the right part)
	Windows Installer writes black text over both, so the text areas stay light; the theme lives
	in a dark side panel and the red blade line. Built from src\ArkaHyte\Assets\app.ico.
	Run with Windows PowerShell 5.1 (System.Drawing).

.EXAMPLE
	powershell -File scripts\New-InstallerArt.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $repoRoot 'src\ArkaHyte\Assets\app.ico'
$outputDir = Join-Path $repoRoot 'installer'

$void = [Drawing.Color]::FromArgb(11, 4, 5)
$ember = [Drawing.Color]::FromArgb(36, 7, 10)
$blade = [Drawing.Color]::FromArgb(255, 31, 51)
$bone = [Drawing.Color]::FromArgb(243, 233, 234)

function New-Canvas([int]$width, [int]$height) {
	$bitmap = New-Object Drawing.Bitmap $width, $height, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
	$graphics = [Drawing.Graphics]::FromImage($bitmap)
	$graphics.SmoothingMode = 'AntiAlias'
	$graphics.InterpolationMode = 'HighQualityBicubic'
	$graphics.TextRenderingHint = 'AntiAliasGridFit'
	$graphics.Clear([Drawing.Color]::White)
	return $bitmap, $graphics
}

# Soft red glow, then the bright core: the lightsaber line of the display
function Add-Blade($graphics, [float]$x1, [float]$y1, [float]$x2, [float]$y2) {
	foreach ($glow in @(@(9, 30), @(5, 60), @(3, 120))) {
		$pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($glow[1], $blade)), $glow[0]
		$graphics.DrawLine($pen, $x1, $y1, $x2, $y2)
		$pen.Dispose()
	}
	$core = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 220, 224)), 1.2
	$graphics.DrawLine($core, $x1, $y1, $x2, $y2)
	$core.Dispose()
}

# Largest entry of the .ico: stored as PNG, which System.Drawing.Icon cannot decode, so it is read
# straight from the ICO directory (16-byte entries after a 6-byte header)
function Get-LargestIconImage([string]$path) {
	$bytes = [IO.File]::ReadAllBytes($path)
	$count = [BitConverter]::ToUInt16($bytes, 4)
	$best = $null
	for ($i = 0; $i -lt $count; $i++) {
		$entry = 6 + 16 * $i
		$size = if ($bytes[$entry] -eq 0) { 256 } else { $bytes[$entry] }
		if (-not $best -or $size -gt $best.Size) {
			$best = @{ Size = $size; Length = [BitConverter]::ToInt32($bytes, $entry + 8); Offset = [BitConverter]::ToInt32($bytes, $entry + 12) }
		}
	}
	$stream = New-Object IO.MemoryStream (, $bytes[$best.Offset..($best.Offset + $best.Length - 1)])
	return [Drawing.Image]::FromStream($stream)
}

$emblem = Get-LargestIconImage $iconPath

# Banner: white title area, dark panel on the right with the emblem, blade along the bottom
$bitmap, $graphics = New-Canvas 493 58
$panel = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Rectangle 400, 0, 93, 58), $void, $ember, 0
$graphics.FillRectangle($panel, 400, 0, 93, 58)
$graphics.DrawImage($emblem, 423, 6, 46, 46)
Add-Blade $graphics 400 0 400 58
Add-Blade $graphics 0 56.5 493 56.5
$bitmap.Save((Join-Path $outputDir 'banner.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$graphics.Dispose(); $bitmap.Dispose(); $panel.Dispose()

# Welcome / finish: dark side panel (164 px, the left part Windows leaves free) with the emblem
$bitmap, $graphics = New-Canvas 493 312
$panel = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Rectangle 0, 0, 164, 312), $ember, $void, 90
$graphics.FillRectangle($panel, 0, 0, 164, 312)
$graphics.DrawImage($emblem, 26, 58, 112, 112)
$font = New-Object Drawing.Font 'Segoe UI Semibold', 15, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$format = New-Object Drawing.StringFormat
$format.Alignment = 'Center'
$textBrush = New-Object Drawing.SolidBrush $bone
$graphics.DrawString('A R K A   H Y T E', $font, $textBrush, (New-Object Drawing.RectangleF 0, 192, 164, 24), $format)
Add-Blade $graphics 163 0 163 312
$bitmap.Save((Join-Path $outputDir 'dialog.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$graphics.Dispose(); $bitmap.Dispose(); $panel.Dispose(); $font.Dispose(); $textBrush.Dispose()

$emblem.Dispose()
Write-Host "Installer art written to $outputDir"
