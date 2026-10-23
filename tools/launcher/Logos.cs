using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json.Nodes;

namespace AcrExtraStages;

/// scripts/menu-location-logos.ps1: the neutral rally-plate logo (485x218 PNG) of every new location with Location.GenLogo,
/// read by `acr-additive menu-locations --logo-dir`.
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
            var colors = loc["LogoColors"]!.ToString().Split(',').Select(x => ColorTranslator.FromHtml(x.Trim())).ToList();
            var png = Path.Combine(outDir, row + ".png");
            Draw(loc["Name"]!.ToString().ToUpperInvariant(), colors, png);
            log.WriteLine($"logo {row} -> {png}");
        }
    }

    static GraphicsPath Plate(float grow)
    {
        // body 6..479 x 58..212 (corner 22) plus a centre arch rising to y 6, grown by `grow` px for the outline layers
        var p = new GraphicsPath();
        float x0 = 6 - grow, x1 = 479 + grow, y0 = 58 - grow, y1 = 212 + grow, r = 22 + grow;
        p.AddArc(x0, y0, 2 * r, 2 * r, 180, 90);
        p.AddLine(x0 + r, y0, 96 - grow, y0);
        p.AddBezier(96 - grow, y0, 140, 6 - grow, 345, 6 - grow, 389 + grow, y0);
        p.AddLine(389 + grow, y0, x1 - r, y0);
        p.AddArc(x1 - 2 * r, y0, 2 * r, 2 * r, 270, 90);
        p.AddArc(x1 - 2 * r, y1 - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x0, y1 - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Font MakeFont(float size)
    {
        Font? f = null;
        foreach (var name in new[] { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" })
        {
            f?.Dispose();
            f = new Font(name, size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (f.Name == name || f.Name == name.Split(' ')[0]) return f;
        }
        return f!;
    }

    static void Draw(string name, List<Color> colors, string png)
    {
        using var bmp = new Bitmap(485, 218, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var outline = new SolidBrush(ColorTranslator.FromHtml("#9AA0A6"))) g.FillPath(outline, Plate(0));
        g.FillPath(Brushes.White, Plate(-2));
        var inner = Plate(-8);
        using (var body = new SolidBrush(colors[0])) g.FillPath(body, inner);
        // stripes: diagonal bands at the right end, clipped to the inner plate
        var stripes = colors.Skip(1).ToList();
        float textRight = 470;
        if (stripes.Count > 0)
        {
            g.SetClip(inner);
            const float w = 18;
            var x = 440 - w * stripes.Count;
            for (int i = 0; i < stripes.Count; i++)
            {
                // the last band runs to the plate's right edge
                var r = i == stripes.Count - 1 ? 520 : x + w;
                using var b = new SolidBrush(stripes[i]);
                g.FillPolygon(b, new[] { new PointF(x + 36, 0), new PointF(r + 36, 0), new PointF(r, 218), new PointF(x, 218) });
                x += w;
            }
            g.ResetClip();
            textRight = 440 - w * stripes.Count - 8;
        }
        var cx = (16 + textRight) / 2;
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var rally = MakeFont(34)) g.DrawString("RALLY", rally, Brushes.White, new RectangleF(cx - 150, 66, 300, 44), fmt);
        float size = 76;
        Font font;
        SizeF sz;
        do
        {
            font = MakeFont(size);
            sz = g.MeasureString(name, font);
            size -= 2;
            if (sz.Width > textRight - 26 && size > 20) font.Dispose();
        } while (sz.Width > textRight - 26 && size > 20);
        g.DrawString(name, font, Brushes.White, new RectangleF(16, 106, textRight - 16, 96), fmt);
        font.Dispose();
        bmp.Save(png, ImageFormat.Png);
    }
}
