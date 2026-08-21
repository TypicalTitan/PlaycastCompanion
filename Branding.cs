using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PlaycastCompanion;

/// <summary>
/// Shared look: palette, app icon, and optional brand art.
///
/// The public repository ships NO vendor logos, fonts, or Playcast artwork —
/// those are trademarks/copyrighted works of their owners. Every card has a
/// drawn, original fallback mark. If you personally hold art you're licensed
/// to use, drop PNGs into Assets\brands\ (gitignored) and they're picked up
/// at build time; see Assets\brands\README.md.
/// </summary>
public static class Branding
{
    /// <summary>Header navy.</summary>
    public static readonly Color Navy = Color.FromArgb(29, 52, 77);

    public static readonly Color Green = Color.FromArgb(0, 190, 110);
    public static readonly Color Amber = Color.FromArgb(235, 150, 30);
    public static readonly Color Slate = Color.FromArgb(120, 130, 145);

    // App-wide dark theme.
    public static readonly Color UiBack = Color.FromArgb(16, 16, 16);
    public static readonly Color UiSurface = Color.FromArgb(30, 30, 30);
    public static readonly Color UiBorder = Color.FromArgb(45, 45, 45);
    public static readonly Color UiText = Color.FromArgb(225, 225, 225);
    public static readonly Color UiTextDim = Color.FromArgb(150, 150, 150);
    public static readonly Color PlaycastGold = Color.FromArgb(246, 197, 72); // accent

    // Per-vendor card palettes (colors are not protectable; names are used
    // nominatively to say which ecosystem a card controls).
    public static readonly Color RazerGreen = Color.FromArgb(68, 214, 44);
    public static readonly Color RazerBlack = Color.Black;
    public static readonly Color RazerSurface = Color.FromArgb(34, 34, 34);
    public static readonly Color RazerText = Color.FromArgb(204, 204, 204);
    public static readonly Color RazerTextDim = Color.FromArgb(153, 153, 153);
    public static readonly Color SteelOrange = Color.FromArgb(255, 82, 0);
    public static readonly Color SteelDark = Color.FromArgb(20, 22, 25);
    public static readonly Color LogiBlue = Color.FromArgb(0, 184, 252);
    public static readonly Color LogiDark = Color.FromArgb(13, 16, 20);
    public static readonly Color CorsairYellow = Color.FromArgb(236, 232, 26);
    public static readonly Color CorsairBlack = Color.FromArgb(10, 10, 10);
    public static readonly Color OpenRgbDark = Color.FromArgb(23, 25, 28);
    public static readonly Color WindowsBlue = Color.FromArgb(0, 120, 212);
    public static readonly Color CardLightText = Color.FromArgb(200, 205, 211);

    /// <summary>App mark: an embedded Assets\squid.png if you supplied one
    /// (not in the public repo), otherwise an original generated icon.</summary>
    public static Image LoadSquid()
    {
        try
        {
            using var stream = typeof(Branding).Assembly
                .GetManifestResourceStream("PlaycastCompanion.Assets.squid.png");
            if (stream != null)
            {
                using var img = Image.FromStream(stream);
                return new Bitmap(img);
            }
        }
        catch
        {
            // fall through to the generated mark
        }
        return DrawFallbackMark(256);
    }

    /// <summary>Original icon: navy disc with a gold "dimmed lamp" ring.</summary>
    private static Image DrawFallbackMark(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = size * 0.06f;
        using (var navy = new SolidBrush(Navy))
            g.FillEllipse(navy, pad, pad, size - 2 * pad, size - 2 * pad);
        using (var ring = new Pen(PlaycastGold, size * 0.09f))
            g.DrawEllipse(ring, size * 0.26f, size * 0.26f, size * 0.48f, size * 0.48f);
        using (var dot = new SolidBrush(PlaycastGold))
            g.FillEllipse(dot, size * 0.42f, size * 0.42f, size * 0.16f, size * 0.16f);
        return bmp;
    }

    /// <summary>Optional user-supplied brand art (Assets\brands\{name}.png), or
    /// null when absent — callers fall back to a drawn mark.</summary>
    public static Image? LoadBrandImage(string name)
    {
        try
        {
            using var stream = typeof(Branding).Assembly
                .GetManifestResourceStream($"PlaycastCompanion.Assets.brands.{name}.png");
            if (stream == null)
                return null;
            using var img = Image.FromStream(stream);
            return new Bitmap(img);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Recolor a monochrome image (alpha preserved).</summary>
    public static Image Tint(Image source, Color color)
    {
        var bmp = new Bitmap(source.Width, source.Height);
        using var g = Graphics.FromImage(bmp);
        var matrix = new ColorMatrix(new float[][]
        {
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    /// <summary>32x32 tray/window icon from the app mark; brightness 1.0 =
    /// normal, lower values render the dimmed "stealth" variant.</summary>
    public static Icon MakeSquidIcon(Image mark, float brightness)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            if (brightness >= 1f)
            {
                g.DrawImage(mark, 0, 0, 32, 32);
            }
            else
            {
                var matrix = new ColorMatrix
                {
                    Matrix00 = brightness,
                    Matrix11 = brightness,
                    Matrix22 = brightness,
                };
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(matrix);
                g.DrawImage(mark, new Rectangle(0, 0, 32, 32),
                    0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attrs);
            }
        }
        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
