using System.Text.Json.Nodes;
using SkiaSharp;

namespace AcrExtraStages;

/// Logos.cs for Linux builds (System.Drawing is Windows-only since .NET 7): the same rally-plate logo (485x218 PNG) of
/// every new location with Location.GenLogo, drawn with SkiaSharp. The csproj compiles exactly one of the two files.
internal static class Logos
{
    public static void Write(IEnumerable<string> specFiles, string outDir, TextWriter log)
    {
        Directory.CreateDirectory(outDir);
        var done = new HashSet<string>();
        foreach (var f in specFiles)
        {
            var loc = JsonNode.Parse(File.ReadAllText(f))?["Location"];
            if (loc?["GenLogo"]?.GetValue<bool>() != true) continue;
            var row = loc["Row"]!.ToString();
            if (!done.Add(row)) continue;
            var colors = loc["LogoColors"]!.ToString().Split(',').Select(x => SKColor.Parse(x.Trim())).ToList();
            var png = Path.Combine(outDir, row + ".png");
            Draw(loc["Name"]!.ToString().ToUpperInvariant(), colors, png);
            log.WriteLine($"logo {row} -> {png}");
        }
    }

    static SKPath Plate(float grow)
    {
        // body 6..479 x 58..212 (corner 22) plus a centre arch rising to y 6, grown by `grow` px for the outline layers
        var p = new SKPath();
        float x0 = 6 - grow, x1 = 479 + grow, y0 = 58 - grow, y1 = 212 + grow, r = 22 + grow;
        p.ArcTo(new SKRect(x0, y0, x0 + 2 * r, y0 + 2 * r), 180, 90, true);
        p.LineTo(96 - grow, y0);
        p.CubicTo(140, 6 - grow, 345, 6 - grow, 389 + grow, y0);
        p.LineTo(x1 - r, y0);
        p.ArcTo(new SKRect(x1 - 2 * r, y0, x1, y0 + 2 * r), 270, 90, false);
        p.ArcTo(new SKRect(x1 - 2 * r, y1 - 2 * r, x1, y1), 0, 90, false);
        p.ArcTo(new SKRect(x0, y1 - 2 * r, x0 + 2 * r, y1), 90, 90, false);
        p.Close();
        return p;
    }

    static SKTypeface Face()
    {
        // Bahnschrift as on Windows when installed, else common Linux sans fonts (fontconfig)
        foreach (var name in new[] { "Bahnschrift", "Noto Sans", "DejaVu Sans", "Liberation Sans" })
        {
            var t = SKFontManager.Default.MatchFamily(name, SKFontStyle.Bold);
            if (t is not null) return t;
        }
        return SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
    }

    static void Centered(SKCanvas g, string text, SKFont font, SKPaint paint, SKRect box)
    {
        var w = font.MeasureText(text);
        font.GetFontMetrics(out var m);
        var y = box.MidY - (m.Ascent + m.Descent) / 2;
        g.DrawText(text, box.MidX - w / 2, y, SKTextAlign.Left, font, paint);
    }

    static void Draw(string name, List<SKColor> colors, string png)
    {
        using var bmp = new SKBitmap(new SKImageInfo(485, 218, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var g = new SKCanvas(bmp))
        {
            g.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            paint.Color = SKColor.Parse("#9AA0A6");
            using (var p = Plate(0)) g.DrawPath(p, paint);
            paint.Color = SKColors.White;
            using (var p = Plate(-2)) g.DrawPath(p, paint);
            using var inner = Plate(-8);
            paint.Color = colors[0];
            g.DrawPath(inner, paint);
            // stripes: diagonal bands at the right end, clipped to the inner plate
            var stripes = colors.Skip(1).ToList();
            float textRight = 470;
            if (stripes.Count > 0)
            {
                g.Save();
                g.ClipPath(inner, antialias: true);
                const float w = 18;
                var x = 440 - w * stripes.Count;
                for (int i = 0; i < stripes.Count; i++)
                {
                    // the last band runs to the plate's right edge
                    var r = i == stripes.Count - 1 ? 520 : x + w;
                    using var band = new SKPath();
                    band.MoveTo(x + 36, 0);
                    band.LineTo(r + 36, 0);
                    band.LineTo(r, 218);
                    band.LineTo(x, 218);
                    band.Close();
                    paint.Color = stripes[i];
                    g.DrawPath(band, paint);
                    x += w;
                }
                g.Restore();
                textRight = 440 - w * stripes.Count - 8;
            }
            var cx = (16 + textRight) / 2;
            paint.Color = SKColors.White;
            using var face = Face();
            using (var rally = new SKFont(face, 34)) Centered(g, "RALLY", rally, paint, new SKRect(cx - 150, 66, cx + 150, 110));
            float size = 76;
            var font = new SKFont(face, size);
            while (font.MeasureText(name) > textRight - 26 && size > 20)
            {
                size -= 2;
                font.Dispose();
                font = new SKFont(face, size);
            }
            Centered(g, name, font, paint, new SKRect(16, 106, textRight, 202));
            font.Dispose();
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(png);
        data.SaveTo(fs);
    }
}
