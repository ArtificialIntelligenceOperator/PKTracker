# Рисует PKTracker.ico (все размеры от 16 до 256) в цветах темы приложения.
# Запускать только при смене иконки: результат лежит в src\PKTracker.App\PKTracker.ico.
[CmdletBinding()]
param(
    [string]$Out,
    [string]$PreviewPng
)

Add-Type -AssemblyName System.Drawing

if (-not $Out) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Out = Join-Path $root '..\src\PKTracker.App\PKTracker.ico'
}

# Цвета темы приложения: тёмный сине-фиолетовый фон с переходом, лавандовый акцент.
$bgTop  = [System.Drawing.ColorTranslator]::FromHtml('#34316A')
$bgLow  = [System.Drawing.ColorTranslator]::FromHtml('#171626')
$border = [System.Drawing.ColorTranslator]::FromHtml('#453F86')
$accent = [System.Drawing.ColorTranslator]::FromHtml('#8E84F5')
$glyph  = [System.Drawing.ColorTranslator]::FromHtml('#EDEAFF')

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # Подложка — скруглённый квадрат, как плитки в Windows
    $r = [math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $m = $s - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($m - $d, 0, $d, $d, 270, 90)
    $path.AddArc($m - $d, $m - $d, $d, $d, 0, 90)
    $path.AddArc(0, $m - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF(0, 0, [single]$s, [single]$s)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgTop, $bgLow, [single]55)
    $g.FillPath($fill, $path)
    $fill.Dispose()
    if ($s -ge 32) {
        $g.DrawPath((New-Object System.Drawing.Pen($border, [single]([math]::Max(1, $s / 32)))), $path)
    }

    # Кольцо покерного чипа
    $inset = $s * 0.13
    $penW = [single]([math]::Max(1.0, $s / 14.0))
    $g.DrawEllipse((New-Object System.Drawing.Pen($accent, $penW)), $inset, $inset, $s - 2 * $inset, $s - 2 * $inset)

    # Пика — глиф ♠ (геометрическая пика из примитивов на мелких размерах разваливается)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $family = 'Segoe UI Symbol'
    if (-not (New-Object System.Drawing.FontFamily($family) -ErrorAction SilentlyContinue)) { $family = 'Segoe UI' }
    $font = New-Object System.Drawing.Font($family, [single]($s * 0.52), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF(0, 0, [single]$s, [single]$s)
    $g.DrawString([char]0x2660, $font, (New-Object System.Drawing.SolidBrush($glyph)), $box, $sf)
    $font.Dispose()

    $g.Dispose()
    return $bmp
}

# Кадр в виде DIB (BITMAPINFOHEADER + BGRA снизу вверх + пустая AND-маска).
# Мелкие размеры пишем именно так: PNG внутри .ico понимает проводник, но System.Drawing.Icon — нет.
function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int][math]::Ceiling($s / 32.0) * 4
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($s * $s * 4 + $maskStride * $s))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $data.Stride, $s * 4) }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose(); $ms.Dispose()
    return , $bytes   # запятая — иначе PowerShell развернёт массив в поток объектов
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 128) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    } else {
        $bytes = ConvertTo-IconDib $bmp
    }
    $frames += [pscustomobject]@{ Size = $s; Bytes = $bytes }
    if ($PreviewPng -and $s -eq 256) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO: заголовок + по записи на размер + сами кадры
$Out = [System.IO.Path]::GetFullPath($Out)
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush(); $w.Dispose(); $fs.Dispose()

Write-Output "Иконка: $Out ($((Get-Item $Out).Length) байт, размеры: $($sizes -join ', '))"
