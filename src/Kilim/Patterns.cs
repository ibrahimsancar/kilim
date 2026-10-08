using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kilim;

public record Pattern(string Id, Color Fringe);

/// <summary>Rug designs, their textures and the colors derived from them.</summary>
static class Patterns
{
    public static readonly Pattern[] All =
    {
        new("sade", Color.FromRgb(0xE4, 0xD8, 0xC3)),
        new("usak", Color.FromRgb(0xE8, 0xDC, 0xC4)),
        new("kilim", Color.FromRgb(0xDD, 0xCF, 0xB6)),
        new("hereke", Color.FromRgb(0xEE, 0xE6, 0xD6)),
    };

    public static Pattern Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static BitmapSource Texture(Pattern p)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/Patterns/{p.Id}.jpg");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Knotted wool strands along one short side. x runs from the rug edge to the tip;
    /// three texels per pixel of rug height keep each strand solid instead of blurring into grey.
    /// </summary>
    public static BitmapSource FringeTexture(Color wool, double rugHeight)
    {
        const int w = 60, spacing = 9;
        var h = (int)(rugHeight * 3);
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (var y = 3; y < h; y += spacing)
        {
            var length = (int)(w * (0.84 + rnd.NextDouble() * 0.16));
            var shade = 0.88 + rnd.NextDouble() * 0.14;
            var sway = (rnd.NextDouble() - 0.5) * 0.08;
            for (var x = 0; x < length; x++)
            {
                // Tied knot near the rug, thinner toward the tip.
                var knot = x < 9 ? 0.84 : 1.0;
                var half = x < 9 ? 3.6 : 2.6;
                var cy = y + (int)Math.Round(sway * x);
                for (var dy = -4; dy <= 4; dy++)
                {
                    var yy = cy + dy;
                    if (yy < 0 || yy >= h) continue;
                    var cover = Math.Clamp(half - Math.Abs(dy) + 0.5, 0, 1);
                    if (cover <= 0) continue;
                    var tipFade = x > length - 6 ? (length - x) / 6.0 : 1;
                    var a = (byte)(255 * cover * tipFade);
                    var i = (yy * w + x) * 4;
                    if (a <= px[i + 3]) continue;
                    // Rounder strands: a little darker at their sides.
                    var s = shade * knot * (1 - Math.Abs(dy) * 0.05);
                    // Premultiplied BGRA.
                    px[i] = (byte)(wool.B * s * a / 255);
                    px[i + 1] = (byte)(wool.G * s * a / 255);
                    px[i + 2] = (byte)(wool.R * s * a / 255);
                    px[i + 3] = a;
                }
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Soft shadow of a w×h rectangle with blur radius r, computed directly. A BlurEffect leaves
    /// bright specks in the nearly transparent pixels of a layered window.
    /// </summary>
    public static BitmapSource SoftShadow(double width, double height, double r)
    {
        var pad = (int)Math.Ceiling(r * 2);
        int w = (int)Math.Ceiling(width) + 2 * pad, h = (int)Math.Ceiling(height) + 2 * pad;
        var s = r / 2;
        double Cover(double v, double a, double b) => 0.5 * (Erf((v - a) / s) - Erf((v - b) / s));
        var fx = new double[w];
        var fy = new double[h];
        for (var x = 0; x < w; x++) fx[x] = Cover(x + 0.5, pad, pad + width);
        for (var y = 0; y < h; y++) fy[y] = Cover(y + 0.5, pad, pad + height);

        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                px[(y * w + x) * 4 + 3] = (byte)Math.Round(255 * fx[x] * fy[y]);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // Abramowitz-Stegun approximation, plenty for a shadow.
    static double Erf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x));
        var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }
}
