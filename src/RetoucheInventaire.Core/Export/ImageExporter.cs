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

    /// <summary>Champs qui portent un trait (texte barré).</summary>
    public static List<Field> StruckFields(Page page) =>
        page.AllFields().Where(f => f.Strikes.Count > 0 && !f.Locked).ToList();

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
