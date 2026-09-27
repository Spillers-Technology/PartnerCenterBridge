# Convert the approved PNG into a multi-resolution Windows icon (Windows/System.Drawing).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$source = [System.Drawing.Image]::FromFile((Join-Path $root 'web/public/brand/app-icon.png'))
try {
    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $memory = [System.IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            ,$memory.ToArray()
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $memory.Dispose() }
    }
    $path = Join-Path $root 'src/PartnerCenterBridge.Api/Assets/app.ico'
    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($path))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = [byte]($sizes[$i] % 256)
            $writer.Write($dimension); $writer.Write($dimension)
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
    Copy-Item -LiteralPath $path -Destination (Join-Path $root 'web/public/brand/favicon.ico')
    Copy-Item -LiteralPath $path -Destination (Join-Path $root 'docs/assets/brand/favicon.ico')
    [IO.File]::WriteAllBytes((Join-Path $root 'web/public/brand/logo-128.png'), [byte[]]$frames[6])
    [IO.File]::WriteAllBytes((Join-Path $root 'docs/assets/brand/logo-128.png'), [byte[]]$frames[6])
} finally { $source.Dispose() }
