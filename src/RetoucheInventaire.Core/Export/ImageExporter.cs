using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Export;

/// <summary>
/// Export image : la photo d'origine, pixel pour pixel, sauf dans les cases modifiées.
/// L'application dessine le nouveau texte sur la page redressée ; ici on recopie uniquement
/// ces cases dans la géométrie de la photo d'origine (perspective et inclinaison inversées).
/// </summary>
public static class ImageExporter
{
    /// <summary>Champs à redessiner : modifiés et non verrouillés.</summary>
    public static List<Field> EditedFields(Page page) =>
        page.AllFields().Where(f => f.IsModified && !f.Locked).ToList();

    /// <summary>Champs qui portent un trait (texte barré) ou une couleur.</summary>
    public static List<Field> StruckFields(Page page) =>
        page.AllFields().Where(f => (f.Strikes.Count > 0 || f.Colors.Count > 0) && !f.Locked).ToList();

    /// <summary>
    /// Couleur du papier autour d'une case : moyenne des pixels clairs de son pourtour,
    /// pour que le fond posé sous le nouveau texte se fonde dans la photo (pas de blanc pur).
    /// </summary>
    public static (byte R, byte G, byte B) PaperColor(Raster image, RectD box)
    {
        var samples = new List<(int L, byte R, byte G, byte B)>();
        int x0 = Math.Clamp((int)box.Left, 0, image.Width - 1), x1 = Math.Clamp((int)box.Right, 0, image.Width - 1);
        int y0 = Math.Clamp((int)box.Top, 0, image.Height - 1), y1 = Math.Clamp((int)box.Bottom, 0, image.Height - 1);
        void Add(int x, int y)
        {
            int i = (y * image.Width + x) * image.Channels;
            byte b = image.Data[i], g = image.Channels == 4 ? image.Data[i + 1] : b, r = image.Channels == 4 ? image.Data[i + 2] : b;
            samples.Add(((r * 77 + g * 150 + b * 29) >> 8, r, g, b));
        }
        for (int x = x0; x <= x1; x++) { Add(x, y0); Add(x, y1); }
        for (int y = y0; y <= y1; y++) { Add(x0, y); Add(x1, y); }
        if (samples.Count == 0) return (255, 255, 255);
        // Les pixels les plus clairs : le papier, pas l'encre ni les filets.
        var light = samples.OrderByDescending(s => s.L).Take(Math.Max(1, samples.Count / 2)).ToList();
        return ((byte)light.Average(s => s.R), (byte)light.Average(s => s.G), (byte)light.Average(s => s.B));
    }

    /// <summary>
    /// Écriture d'origine mise en couleur : dans chaque rectangle, chaque canal garde le plus clair de l'image
    /// et de la couleur (l'encre noire prend la couleur, le papier reste tel quel). Renvoie une image BGRA.
    /// </summary>
    public static Raster Tint(Raster image, IEnumerable<(int X0, int Y0, int X1, int Y1, string Color)> rects)
    {
        var list = rects.ToList();
        if (list.Count == 0) return image;
        var src = image.ToBgra();
        var output = new Raster(src.Width, src.Height, 4, (byte[])src.Data.Clone());
        foreach (var (x0, y0, x1, y1, color) in list)
        {
            var (r, g, b) = Strikes.TextRgb(color);
            for (int y = Math.Max(0, y0); y < Math.Min(output.Height, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(output.Width, x1); x++)
                {
                    int o = (y * output.Width + x) * 4;
                    output.Data[o] = Math.Max(output.Data[o], b);
                    output.Data[o + 1] = Math.Max(output.Data[o + 1], g);
                    output.Data[o + 2] = Math.Max(output.Data[o + 2], r);
                }
        }
        return output;
    }

    /// <summary>
    /// Fond tramé (gris des totaux et des en-têtes) : un morceau propre de la même rangée, sans texte ni filet,
    /// à répéter sous le texte retapé (un aplat blanc se verrait). Null si le fond est du papier uni.
    /// <paramref name="dy"/> : décalage vertical de la page affichée par rapport à la page d'origine.
    /// </summary>
    public static RectD? CleanPatch(Raster image, Page page, PrintedLine line, Field f, double dy = 0)
    {
        var bx = f.Box with { Y = f.Box.Y + dy };
        int x0 = Math.Max(0, (int)Math.Floor(bx.Left)), x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(bx.Right) - 1);
        int y0 = Math.Max(0, (int)Math.Floor(bx.Top)), y1 = Math.Min(image.Height - 1, (int)Math.Ceiling(bx.Bottom) - 1);
        if (x1 <= x0 || y1 <= y0) return null;
        int G(int x, int y)
        {
            int o = (y * image.Width + x) * image.Channels;
            return image.Channels == 1 ? image.Data[o] : (image.Data[o] * 29 + image.Data[o + 1] * 150 + image.Data[o + 2] * 77) >> 8;
        }
        long sum = 0; int n = 0;
        for (int x = x0; x <= x1; x += 2) { sum += G(x, y0) + G(x, y1); n += 2; }
        if ((double)sum / n >= 245) return null; // papier uni
        var table = page.Tables.FirstOrDefault(t => t.Rows.Any(r => r.Lines.Contains(line)));
        if (table is null || table.ColumnXs.Count == 0) return null;
        var row = table.Rows.First(r => r.Lines.Contains(line));
        var ink = row.Lines.SelectMany(l => l.Cells).Where(c => !c.Ink.IsEmpty).Select(c => c.Ink with { Y = c.Ink.Y + dy }).ToList();
        int h = y1 - y0 + 1, pw = Math.Min(48, x1 - x0 + 1);
        RectD? best = null;
        long bestSum = -1;
        for (int x = (int)Math.Ceiling(table.ColumnXs[0]) + 4; x + pw < table.ColumnXs[^1] - 4; x += 2)
        {
            if (table.ColumnXs.Any(cx => cx > x - 5 && cx < x + pw + 5)) continue;
            if (ink.Any(i => i.Left < x + pw + 2 && x - 2 < i.Right && i.Top < y0 + h && y0 < i.Bottom)) continue;
            long s2 = 0;
            for (int y = y0; y <= y1; y += 2) for (int xx = x; xx < x + pw; xx += 2) s2 += G(xx, y);
            if (s2 > bestSum) { bestSum = s2; best = new RectD(x, y0, pw, h); }
        }
        return best;
    }

    /// <summary>Image de la taille de <paramref name="box"/> : le morceau <paramref name="patch"/> répété en largeur (BGRA).</summary>
    public static Raster TilePatch(Raster image, RectD patch, int width, int height)
    {
        var src = image.ToBgra();
        var output = new Raster(Math.Max(1, width), Math.Max(1, height), 4, new byte[Math.Max(1, width) * Math.Max(1, height) * 4]);
        int px = (int)patch.X, py = (int)patch.Y, pw = Math.Max(1, (int)patch.Width), ph = Math.Max(1, (int)patch.Height);
        for (int y = 0; y < output.Height; y++)
            for (int x = 0; x < output.Width; x++)
            {
                int sx = Math.Clamp(px + x % pw, 0, src.Width - 1), sy = Math.Clamp(py + Math.Min(y, ph - 1), 0, src.Height - 1);
                Array.Copy(src.Data, (sy * src.Width + sx) * 4, output.Data, (y * output.Width + x) * 4, 4);
            }
        return output;
    }

    /// <summary>
    /// Recopie les cases modifiées (et les numéros de lot) de la page redressée (déjà retouchée par l'application)
    /// dans la photo d'origine orientée. Tout le reste de la photo est inchangé.
    /// </summary>
    public static Raster ComposeIntoSource(Raster orientedSource, Page page, Raster editedPage, bool lotFrame = true)
    {
        var src = orientedSource.ToBgra();
        var edited = editedPage.ToBgra();
        var output = new Raster(src.Width, src.Height, 4, (byte[])src.Data.Clone());
        var boxes = EditedFields(page).Concat(StruckFields(page)).Select(f => f.Box).Concat(LotColumn.Regions(page, lotFrame)).ToList();
        if (boxes.Count == 0) return output;

        var crop = page.Crop ?? Quad.FullImage(src.Width, src.Height);
        int W = page.Width, H = page.Height;
        double a = page.Skew * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), cx = W / 2.0, cy = H / 2.0;

        // Page redressée → photo (comme au rendu) et photo → page redressée (inverse).
        var toSource = ImageOps.Homography(crop, W, H);
        var rect = new[] { new PointD(0, 0), new PointD(W - 1, 0), new PointD(W - 1, H - 1), new PointD(0, H - 1) };
        var toFlat = ImageOps.Homography(crop.ToArray(), rect);
        PointD Forward(double u, double v)
        {
            double du = u - cx, dv = v - cy;
            return toSource(cx + cos * du - sin * dv, cy + sin * du + cos * dv);
        }
        PointD Backward(double x, double y)
        {
            var p = toFlat(x, y);
            double du = p.X - cx, dv = p.Y - cy;
            return new PointD(cx + cos * du + sin * dv, cy - sin * du + cos * dv);
        }

        foreach (var box in boxes)
        {
            var corners = new[] { Forward(box.Left, box.Top), Forward(box.Right, box.Top), Forward(box.Right, box.Bottom), Forward(box.Left, box.Bottom) };
            int sx0 = Math.Max(0, (int)Math.Floor(corners.Min(p => p.X)) - 1), sx1 = Math.Min(src.Width - 1, (int)Math.Ceiling(corners.Max(p => p.X)) + 1);
            int sy0 = Math.Max(0, (int)Math.Floor(corners.Min(p => p.Y)) - 1), sy1 = Math.Min(src.Height - 1, (int)Math.Ceiling(corners.Max(p => p.Y)) + 1);
            for (int y = sy0; y <= sy1; y++)
                for (int x = sx0; x <= sx1; x++)
                {
                    var q = Backward(x, y);
                    if (!box.Contains(q.X, q.Y)) continue;
                    Sample(edited, q.X, q.Y, output.Data, (y * src.Width + x) * 4);
                }
        }
        return output;
    }

    private static void Sample(Raster img, double x, double y, byte[] dst, int o)
    {
        x = Math.Clamp(x, 0, img.Width - 1);
        y = Math.Clamp(y, 0, img.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 4; c++)
        {
            double top = img.Data[(y0 * img.Width + x0) * 4 + c] * (1 - fx) + img.Data[(y0 * img.Width + x1) * 4 + c] * fx;
            double bot = img.Data[(y1 * img.Width + x0) * 4 + c] * (1 - fx) + img.Data[(y1 * img.Width + x1) * 4 + c] * fx;
            dst[o + c] = (byte)Math.Round(top * (1 - fy) + bot * fy);
        }
    }
}
