<#
.SYNOPSIS
	Draws the HyteDisplay emblem (kyber ring + vertical lightsaber) and writes a multi-size .ico.

.DESCRIPTION
	Each size is rendered on its own (not downscaled) so the small tray sizes stay crisp.
	Sizes up to 64 px are stored as classic 32-bit DIB entries (System.Drawing.Icon, used by the
	tray icon, cannot read small PNG entries); 128 and 256 px are stored as PNG to keep the file small.
	Run with Windows PowerShell 5.1 (System.Drawing).

	With -SourceImage, the icon is built from your own picture (PNG with transparency is best)
	instead of the drawn emblem; -Recolor tints every opaque pixel (handy for a black logo).
	-LumaMask turns bright pixels into the logo and drops the dark ones, so a light emblem on a
	dark background becomes a clean tinted silhouette; -Background dark puts it on a full-size dark disc.

.EXAMPLE
	powershell -File scripts\New-AppIcon.ps1
	powershell -File scripts\New-AppIcon.ps1 -SourceImage logo.png -Recolor '#ff1f33'
	powershell -File scripts\New-AppIcon.ps1 -SourceImage logo.png -Background none
#>
[CmdletBinding()]
param(
	[string]$OutputPath = '',
	[string]$PreviewPath = '',
	[string]$SourceImage = '',
	[ValidateSet('disc', 'dark', 'none')]
	[string]$Background = 'disc',
	[string]$Recolor = '',
	# Keep only the bright parts of the source as the logo shape (alpha from brightness)
	[switch]$LumaMask
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) {
	$OutputPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ArkaHyte\Assets\app.ico'
}

function New-Pen([System.Drawing.Color]$color, [float]$width) {
	$pen = New-Object System.Drawing.Pen $color, $width
	$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
	$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
	return $pen
}

function New-EmblemBitmap([int]$size) {
	$bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bitmap)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)

	$s = [float]$size
	$small = $size -le 32
	$c = $s / 2

	# Dark disc with a faint red core glow
	$disc = New-Object System.Drawing.Drawing2D.GraphicsPath
	$disc.AddEllipse(0.02 * $s, 0.02 * $s, 0.96 * $s, 0.96 * $s)
	$fill = New-Object System.Drawing.Drawing2D.PathGradientBrush $disc
	$fill.CenterColor = [System.Drawing.Color]::FromArgb(255, 58, 6, 12)
	$fill.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 8, 2, 3))
	$g.FillPath($fill, $disc)

	# Kyber ring: closed circle wrapped in radial spikes, like the CPU widget of the display
	$ringRadius = 0.27 * $s
	$ringBox = New-Object System.Drawing.RectangleF ($c - $ringRadius), ($c - $ringRadius), (2 * $ringRadius), (2 * $ringRadius)
	if (-not $small) {
		$spikes = 30
		for ($i = 0; $i -lt $spikes; $i++) {
			$angle = 2 * [Math]::PI * $i / $spikes
			# Deterministic irregular lengths so the ring looks like a live load history
			$length = (0.035 + 0.05 * [Math]::Abs([Math]::Sin($i * 1.7) * [Math]::Cos($i * 0.6))) * $s
			$inner = $ringRadius + 0.05 * $s
			$alpha = [int](120 + 120 * [Math]::Abs([Math]::Sin($i * 0.9)))
			$spike = New-Pen ([System.Drawing.Color]::FromArgb($alpha, 255, 40, 60)) ([float]([Math]::Max(1.0, 0.016 * $s)))
			$g.DrawLine($spike,
				[float]($c + [Math]::Cos($angle) * $inner), [float]($c + [Math]::Sin($angle) * $inner),
				[float]($c + [Math]::Cos($angle) * ($inner + $length)), [float]($c + [Math]::Sin($angle) * ($inner + $length)))
			$spike.Dispose()
		}
		foreach ($layer in @(@(0.13, 45), @(0.08, 90))) {
			$glow = New-Pen ([System.Drawing.Color]::FromArgb($layer[1], 255, 31, 51)) ([float]($layer[0] * $s))
			$g.DrawEllipse($glow, $ringBox)
			$glow.Dispose()
		}
	}
	# At tray sizes a ring crossed by a diagonal reads as a "prohibited" sign: keep only the saber there
	if (-not $small) {
		$ring = New-Pen ([System.Drawing.Color]::FromArgb(255, 255, 36, 56)) ([float](0.045 * $s))
		$g.DrawEllipse($ring, $ringBox)
		$ring.Dispose()
	}

	# Diagonal blade crossing the ring, tip top-right, white core
	$tipX = 0.82 * $s
	$tipY = 0.18 * $s
	$baseX = 0.36 * $s
	$baseY = 0.64 * $s
	# Halo kept at every size: on its own at tray sizes the blade must still read as glowing
	# Unary comma keeps the single-layer list nested (PowerShell would flatten @(@(a, b)))
	$glowLayers = if ($small) { , @(0.26, 70) } else { @(@(0.17, 45), @(0.11, 95)) }
	if ($small) {
		$glowLayers = @(, $glowLayers)
	}
	foreach ($layer in $glowLayers) {
		$glow = New-Pen ([System.Drawing.Color]::FromArgb($layer[1], 255, 31, 51)) ([float]($layer[0] * $s))
		$g.DrawLine($glow, $baseX, $baseY, $tipX, $tipY)
		$glow.Dispose()
	}
	$blade = New-Pen ([System.Drawing.Color]::FromArgb(255, 255, 40, 60)) ([float]([Math]::Max(2.4, 0.07 * $s)))
	$g.DrawLine($blade, $baseX, $baseY, $tipX, $tipY)
	$blade.Dispose()
	$core = New-Pen ([System.Drawing.Color]::FromArgb(255, 255, 240, 240)) ([float]([Math]::Max(0.8, 0.026 * $s)))
	$g.DrawLine($core, $baseX + 0.01 * $s, $baseY - 0.01 * $s, $tipX - 0.01 * $s, $tipY + 0.01 * $s)
	$core.Dispose()

	# Chrome hilt continuing the blade towards the bottom-left, drawn in a rotated frame
	$hiltLength = 0.24 * $s
	$hiltWidth = [Math]::Max(2.6, 0.095 * $s)
	$g.TranslateTransform([float]($baseX - $hiltLength / 2 * 0.7071), [float]($baseY + $hiltLength / 2 * 0.7071))
	$g.RotateTransform(135)
	$hiltRect = New-Object System.Drawing.RectangleF (-$hiltLength / 2), (-$hiltWidth / 2), $hiltLength, $hiltWidth
	$metal = New-Object System.Drawing.Drawing2D.LinearGradientBrush $hiltRect, ([System.Drawing.Color]::Gray), ([System.Drawing.Color]::DimGray), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
	$blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
	$blend.Colors = @([System.Drawing.Color]::FromArgb(255, 70, 70, 76), [System.Drawing.Color]::FromArgb(255, 238, 238, 242), [System.Drawing.Color]::FromArgb(255, 46, 46, 52))
	$blend.Positions = @(0.0, 0.42, 1.0)
	$metal.InterpolationColors = $blend
	$g.FillRectangle($metal, $hiltRect)
	if (-not $small) {
		$grip = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 14, 14, 16))
		$g.FillRectangle($grip, (0.0 * $hiltLength), (-$hiltWidth / 2), (0.32 * $hiltLength), $hiltWidth)
		$grip.Dispose()
		$button = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 31, 51))
		$g.FillEllipse($button, (-0.32 * $hiltLength), (-$hiltWidth / 2 - 0.022 * $s), (0.04 * $s), (0.04 * $s))
		$button.Dispose()
	}
	$g.ResetTransform()

	# Thin red rim so the disc reads on light taskbars too
	$rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 255, 50, 64)), ([float]([Math]::Max(1.0, 0.018 * $s)))
	$g.DrawEllipse($rim, 0.03 * $s, 0.03 * $s, 0.94 * $s, 0.94 * $s)
	$rim.Dispose()

	$metal.Dispose()
	$fill.Dispose()
	$disc.Dispose()
	$g.Dispose()
	return $bitmap
}

# Dark disc + red rim used behind a user-supplied logo so it reads on any taskbar colour
function Add-DiscBackground([System.Drawing.Graphics]$g, [float]$s) {
	$disc = New-Object System.Drawing.Drawing2D.GraphicsPath
	$disc.AddEllipse(0.02 * $s, 0.02 * $s, 0.96 * $s, 0.96 * $s)
	$fill = New-Object System.Drawing.Drawing2D.PathGradientBrush $disc
	$fill.CenterColor = [System.Drawing.Color]::FromArgb(255, 48, 6, 10)
	$fill.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 8, 2, 3))
	$g.FillPath($fill, $disc)
	$rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(180, 255, 50, 64)), ([float]([Math]::Max(1.0, 0.022 * $s)))
	$g.DrawEllipse($rim, 0.03 * $s, 0.03 * $s, 0.94 * $s, 0.94 * $s)
	$rim.Dispose()
	$fill.Dispose()
	$disc.Dispose()
}

# Sets the colour of every non-transparent pixel, keeping its alpha (for single-colour logos).
# With -LumaMask the alpha comes from brightness: light metal stays, dark background fades out.
function Set-BitmapTint([System.Drawing.Bitmap]$bitmap, [System.Drawing.Color]$color) {
	$rect = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
	$data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$bytes = New-Object byte[] ($data.Stride * $bitmap.Height)
	[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
	for ($i = 0; $i -lt $bytes.Length; $i += 4) {
		if ($bytes[$i + 3] -eq 0) {
			continue
		}
		if ($LumaMask) {
			$luma = 0.299 * $bytes[$i + 2] + 0.587 * $bytes[$i + 1] + 0.114 * $bytes[$i]
			$coverage = [Math]::Min(1.0, [Math]::Max(0.0, ($luma - 105) / 85.0))
			$bytes[$i + 3] = [byte]([Math]::Round($bytes[$i + 3] * $coverage))
		}
		$bytes[$i] = $color.B
		$bytes[$i + 1] = $color.G
		$bytes[$i + 2] = $color.R
	}
	[System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
	$bitmap.UnlockBits($data)
}

function New-LogoBitmap([System.Drawing.Image]$source, [int]$size) {
	$s = [float]$size
	$bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bitmap)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)
	if ($Background -eq 'disc') {
		Add-DiscBackground $g $s
	}
	elseif ($Background -eq 'dark') {
		# Full-size dark disc exactly behind a round logo, in the panels' red-black
		$disc = New-Object System.Drawing.Drawing2D.GraphicsPath
		$disc.AddEllipse(0.5, 0.5, $s - 1, $s - 1)
		$fill = New-Object System.Drawing.Drawing2D.PathGradientBrush $disc
		$fill.CenterColor = [System.Drawing.Color]::FromArgb(255, 42, 6, 9)
		$fill.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 12, 3, 4))
		$g.FillPath($fill, $disc)
		$fill.Dispose()
		$disc.Dispose()
	}

	# Logo drawn on its own layer so tinting never touches the disc
	$padding = if ($Background -eq 'disc') { 0.17 * $s } else { 0.0 }
	$box = $s - 2 * $padding
	$scale = [Math]::Min($box / $source.Width, $box / $source.Height)
	$width = [int][Math]::Max(1, [Math]::Round($source.Width * $scale))
	$height = [int][Math]::Max(1, [Math]::Round($source.Height * $scale))
	$layer = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$lg = [System.Drawing.Graphics]::FromImage($layer)
	$lg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$lg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$lg.Clear([System.Drawing.Color]::Transparent)
	$lg.DrawImage($source, [int](($s - $width) / 2), [int](($s - $height) / 2), $width, $height)
	$lg.Dispose()
	if ($Recolor) {
		Set-BitmapTint $layer ([System.Drawing.ColorTranslator]::FromHtml($Recolor))
	}
	$g.DrawImage($layer, 0, 0, $size, $size)
	$layer.Dispose()
	$g.Dispose()
	return $bitmap
}

# BITMAPINFOHEADER (height doubled for the XOR + AND masks), bottom-up BGRA rows, empty AND mask
function ConvertTo-DibEntry([System.Drawing.Bitmap]$bitmap) {
	$size = $bitmap.Width
	$rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
	$data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$pixels = New-Object byte[] ($size * $size * 4)
	for ($row = 0; $row -lt $size; $row++) {
		$source = [IntPtr]($data.Scan0.ToInt64() + $row * $data.Stride)
		[System.Runtime.InteropServices.Marshal]::Copy($source, $pixels, ($size - 1 - $row) * $size * 4, $size * 4)
	}
	$bitmap.UnlockBits($data)

	$maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
	$stream = New-Object System.IO.MemoryStream
	$writer = New-Object System.IO.BinaryWriter $stream
	$writer.Write([uint32]40)
	$writer.Write([int32]$size)
	$writer.Write([int32]($size * 2))
	$writer.Write([uint16]1)
	$writer.Write([uint16]32)
	$writer.Write([uint32]0)
	$writer.Write([uint32]($pixels.Length + $maskStride * $size))
	$writer.Write([int32]0)
	$writer.Write([int32]0)
	$writer.Write([uint32]0)
	$writer.Write([uint32]0)
	$writer.Write($pixels)
	$writer.Write((New-Object byte[] ($maskStride * $size)))
	$writer.Flush()
	# Leading comma: return the byte[] as one object instead of letting PowerShell unroll it
	return , $stream.ToArray()
}

$source = $null
if ($SourceImage) {
	$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourceImage).Path)
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = foreach ($size in $sizes) {
	$bitmap = if ($source) { New-LogoBitmap $source $size } else { New-EmblemBitmap $size }
	if ($size -le 64) {
		[byte[]]$bytes = ConvertTo-DibEntry $bitmap
	}
	else {
		$stream = New-Object System.IO.MemoryStream
		$bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
		$bytes = $stream.ToArray()
	}
	if ($PreviewPath -and $size -eq 256) {
		$bitmap.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
	}
	$bitmap.Dispose()
	[pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes }
}
if ($source) {
	$source.Dispose()
}

# ICONDIR + ICONDIRENTRY[] + PNG payloads
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$file = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
	$dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
	$writer.Write([byte]$dimension)
	$writer.Write([byte]$dimension)
	$writer.Write([byte]0)
	$writer.Write([byte]0)
	$writer.Write([uint16]1)
	$writer.Write([uint16]32)
	$writer.Write([uint32]$entry.Bytes.Length)
	$writer.Write([uint32]$offset)
	$offset += $entry.Bytes.Length
}
foreach ($entry in $entries) {
	$writer.Write($entry.Bytes)
}
$writer.Dispose()
Write-Host "Icon written to $OutputPath ($((Get-Item $OutputPath).Length) bytes)"
