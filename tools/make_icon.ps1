# Draws the OpenPedalHub emblem (silver orb, green ring) and writes OpenPedalHub\app.ico.
# That one file is the icon of the program, its window, the taskbar button and the tray.
# To use a different logo, replace app.ico with your own multi-size .ico and rebuild.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 32.0
    $body = New-Object System.Drawing.RectangleF (1 * $k), (1 * $k), (30 * $k), (30 * $k)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $body, ([System.Drawing.Color]::White), ([System.Drawing.Color]::FromArgb(0xB4, 0xBC, 0xC3)), 90.0
    $g.FillEllipse($fill, $body)
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x7C, 0x86, 0x8E)), ([float](1.2 * $k))
    $g.DrawEllipse($edge, $body)
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x5A, 0xB0, 0x18)), ([float](5 * $k))
    $g.DrawEllipse($ring, [float](8 * $k), [float](8 * $k), [float](16 * $k), [float](16 * $k))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # The 256 px picture is stored as PNG, as Windows expects.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Smaller pictures use the classic uncompressed form, which every program reads correctly.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
        $data = $bmp.LockBits($rect, 'ReadOnly', 'Format32bppArgb')
        $row = New-Object byte[] ($s * 4)
        for ($y = $s - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
            $bw.Write($row)
        }
        $bmp.UnlockBits($data)
        $maskRow = [int]([math]::Ceiling($s / 32.0)) * 4
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = Join-Path $PSScriptRoot '..\OpenPedalHub\app.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
"wrote $((Resolve-Path $out).Path | Split-Path -Leaf): $((Get-Item $out).Length) bytes, $($sizes.Count) sizes"
