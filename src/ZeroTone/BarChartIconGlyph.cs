using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZeroTone;

/// <summary>
/// Renders the bar-chart glyph at an exact pixel size with hard binary alpha.
/// Used when the taskbar slot (e.g. 30×30 @ 125%) is not in the multi-res .ico
/// (green / amber / red) — soft-scaling 28/32 would anti-alias the edges.
/// Standard sizes come from the embedded multi-res masters.
/// </summary>
internal static class BarChartIconGlyph
{
    /// <summary>
    /// Running / keep-alive active (matches <c>bar-chart-green.ico</c>).
    /// The same green is the executable / shortcut / mixer identity.
    /// </summary>
    public static readonly Color RunningGreen = Color.FromArgb(0xFF, 0x27, 0xAE, 0x60);

    /// <summary>
    /// Starting / Reconnecting (engaged, stream not proven). Matches
    /// <c>bar-chart-amber.ico</c>. Saturated so the tray glyph stays readable
    /// on both Light and Dark taskbars.
    /// </summary>
    public static readonly Color UnprovenAmber = Color.FromArgb(0xFF, 0xE6, 0xA0, 0x00);

    /// <summary>Stopped (matches <c>bar-chart-red.ico</c>).</summary>
    public static readonly Color StoppedRed = Color.FromArgb(0xFF, 0xFF, 0x00, 0x00);

    // 16×16 unit grid matching the multi-res .ico masters. Stroke thickness = 1 unit.
    private static readonly (int X, int Y, int W, int H)[] Bars =
    [
        (0, 4, 4, 12),  // left  — medium
        (6, 8, 4, 8),   // mid   — short
        (12, 0, 4, 16), // right — tall
    ];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Builds an owned <see cref="Icon"/> at exactly <paramref name="size"/>×size (no soft scaling).
    /// </summary>
    public static Icon CreateIcon(int size, Color color)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Icon size must be ≥ 1.");
        }

        using var bmp = Render(size, color);
        return CreateIconFromBitmap(bmp);
    }

    /// <summary>
    /// Outline bars into a 32bpp ARGB bitmap. Fully transparent or fully opaque only
    /// (no intermediate alpha — avoids soft edges).
    /// </summary>
    public static Bitmap Render(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var stroke = Math.Max(1, size / 16);

        for (var i = 0; i < Bars.Length; i++)
        {
            var (ux, uy, uw, uh) = Bars[i];
            var x = UnitToPx(ux, size);
            var y = UnitToPx(uy, size);
            var r = UnitToPx(ux + uw, size);
            var b = UnitToPx(uy + uh, size);
            var w = r - x;
            var h = b - y;
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            FillRect(bmp, x, y, w, h, color);

            var innerW = w - 2 * stroke;
            var innerH = h - 2 * stroke;
            if (innerW > 0 && innerH > 0)
            {
                FillRect(bmp, x + stroke, y + stroke, innerW, innerH, Color.Transparent);
            }
        }

        return bmp;
    }

    private static int UnitToPx(int unit, int size) => unit * size / 16;

    private static void FillRect(Bitmap bmp, int x, int y, int w, int h, Color c)
    {
        var x2 = Math.Min(x + w, bmp.Width);
        var y2 = Math.Min(y + h, bmp.Height);
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        for (var py = y0; py < y2; py++)
        {
            for (var px = x0; px < x2; px++)
            {
                bmp.SetPixel(px, py, c);
            }
        }
    }

    /// <summary>
    /// Bitmap → owned Icon. <see cref="Bitmap.GetHicon"/> returns a handle we must
    /// destroy after cloning into a managed Icon.
    /// </summary>
    private static Icon CreateIconFromBitmap(Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
