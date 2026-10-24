using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Export;

/// <summary>
/// Colonne « Lot » tracée dans la marge gauche, comme si elle avait été imprimée avec le tableau :
/// trait vertical à gauche, filets horizontaux du tableau prolongés, chaque case au même fond que sa rangée
/// (gris hachuré de l'en-tête et des totaux recopié depuis la rangée). Épaisseur et couleur des traits mesurées sur le scan.
/// Rien n'est déplacé : la colonne n'occupe que la marge blanche.
/// Calculs en entiers uniquement : résultat identique, pixel pour pixel, à la version iPhone/iPad.
/// </summary>
public static class LotColumn
{
    /// <summary>Un tableau à encadrer et ce qu'il faut tracer.</summary>
    public sealed record Frame(Table Table, int Left, int Right, int Top, int Bottom, int VThick, int HThick, byte[] Color);

    private static int R(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>Tableaux à grille qui portent au moins un numéro de lot.</summary>
    public static List<Table> FramedTables(Page page) =>
        page.Tables.Where(t => t.HasGrid && t.ColumnXs.Count >= 2 && t.RowYs.Count >= 2
                               && t.Rows.SelectMany(r => r.Lines).Any(l => l.Lot.Length > 0)).ToList();

    /// <summary>Signature de ce qui est tracé (pour savoir s'il faut redessiner l'écran).</summary>
    public static string Signature(Page page, bool enabled) =>
        !enabled ? "" : string.Join(",", FramedTables(page).Select(t => page.Tables.IndexOf(t))) + "/" + Lots.ColumnWidth(page);

    public static List<Frame> Frames(Raster image, Page page)
    {
        double w = Lots.ColumnWidth(page);
        var frames = new List<Frame>();
        foreach (var t in FramedTables(page))
        {
            int right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
            if (right - left < 8) continue;
            var (vt, ht, color) = MeasureRules(image, t);
            frames.Add(new Frame(t, left, right, R(t.RowYs[0]), R(t.RowYs[^1]), vt, ht, color));
        }
        return frames;
    }

    /// <summary>Zones de la page redressée qui changent (à recopier dans la photo d'origine).</summary>
    public static List<RectD> Regions(Page page, bool enabled)
    {
        var boxes = new List<RectD>();
        var framed = enabled ? FramedTables(page) : [];
        double w = Lots.ColumnWidth(page);
        foreach (var t in framed)
            boxes.Add(RectD.FromEdges(t.ColumnXs[0] - w - 8, t.RowYs[0] - 8, t.ColumnXs[0] - 1, t.RowYs[^1] + 8));
        foreach (var t in page.Tables.Where(t => !framed.Contains(t)))
            foreach (var l in t.Rows.SelectMany(r => r.Lines).Where(l => l.Lot.Length > 0))
                boxes.Add(Lots.NumberBox(t, l, w));
        return boxes;
    }

    /// <summary>Copie de la page redressée avec les colonnes tracées (rien d'autre ne change).</summary>
    public static Raster Draw(Raster image, Page page)
    {
        var output = new Raster(image.Width, image.Height, image.Channels, (byte[])image.Data.Clone());
        foreach (var f in Frames(image, page))
        {
            DrawBackgrounds(image, output, f);
            int half = f.VThick / 2;
            Fill(output, f.Left - half, f.Top - f.HThick / 2, f.VThick, f.Bottom - f.Top + f.HThick, f.Color);
            foreach (var y in f.Table.RowYs)
                Fill(output, f.Left - half, R(y) - f.HThick / 2, f.Right - (f.Left - half), f.HThick, f.Color);
        }
        return output;
    }

    /// <summary>
    /// Fond de chaque case de la colonne : le même que celui de la rangée (gris hachuré de l'en-tête
    /// ou des totaux, papier ailleurs), recopié depuis une partie de la rangée sans texte.
    /// </summary>
    private static void DrawBackgrounds(Raster source, Raster output, Frame f)
    {
        var t = f.Table;
        int x0 = f.Left - f.VThick / 2 + f.VThick, x1 = f.Right - f.VThick / 2;   // intérieur de la colonne
        int w = x1 - x0;
        if (w <= 0) return;
        for (int r = 0; r + 1 < t.RowYs.Count; r++)
        {
            int y0 = R(t.RowYs[r]) - f.HThick / 2 + f.HThick, y1 = R(t.RowYs[r + 1]) - f.HThick / 2;
            if (y1 <= y0) continue;
            var ink = (r < t.Rows.Count ? t.Rows[r].Lines : [])
                .SelectMany(l => l.Cells).Where(c => !c.Ink.IsEmpty).Select(c => c.Ink).ToList();
            int start = R(t.ColumnXs[0]) + f.VThick + 2, end = R(t.ColumnXs[1]) - f.VThick - 2 - w;
            for (int sx = start; sx <= end; sx += 2)
            {
                var patch = new RectD(sx - 3, y0, w + 6, y1 - y0);
                if (ink.Any(i => i.IntersectsWith(patch))) continue;
                Copy(source, output, sx, y0, x0, y0, w, y1 - y0);
                break;
            }
        }
    }

    private static void Copy(Raster source, Raster output, int sx, int sy, int dx, int dy, int w, int h)
    {
        int ch = source.Channels;
        for (int y = 0; y < h; y++)
        {
            int ys = sy + y, yd = dy + y;
            if (ys < 0 || ys >= source.Height || yd < 0 || yd >= source.Height) continue;
            for (int x = 0; x < w; x++)
            {
                int xs = sx + x, xd = dx + x;
                if (xs < 0 || xs >= source.Width || xd < 0 || xd >= source.Width) continue;
                Array.Copy(source.Data, (ys * source.Width + xs) * ch, output.Data, (yd * source.Width + xd) * ch, ch);
            }
        }
    }

    private static void Fill(Raster img, int x, int y, int w, int h, byte[] color)
    {
        int ch = img.Channels;
        for (int yy = Math.Max(0, y); yy < Math.Min(img.Height, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(img.Width, x + w); xx++)
            {
                int o = (yy * img.Width + xx) * ch;
                if (ch == 1) img.Data[o] = color[3];
                else { img.Data[o] = color[0]; img.Data[o + 1] = color[1]; img.Data[o + 2] = color[2]; img.Data[o + 3] = 255; }
            }
    }

    private static int Gray(Raster img, int x, int y)
    {
        int o = (y * img.Width + x) * img.Channels;
        return img.Channels == 1 ? img.Data[o] : (img.Data[o] * 29 + img.Data[o + 1] * 150 + img.Data[o + 2] * 77) >> 8;
    }

    /// <summary>
    /// Épaisseur des filets (vertical gauche, horizontaux) et leur couleur, mesurées sur l'image :
    /// les traits ajoutés ressemblent à ceux imprimés. Couleur : moyenne du cœur des traits, 3 octets dans l'ordre de l'image + gris.
    /// </summary>
    public static (int VThick, int HThick, byte[] Color) MeasureRules(Raster img, Table t)
    {
        var vRuns = new List<int>();
        var hRuns = new List<int>();
        long s0 = 0, s1 = 0, s2 = 0, sg = 0, n = 0;
        int ch = img.Channels;

        void Take(int x, int y)
        {
            int o = (y * img.Width + x) * ch;
            s0 += img.Data[o];
            s1 += ch == 1 ? img.Data[o] : img.Data[o + 1];
            s2 += ch == 1 ? img.Data[o] : img.Data[o + 2];
            sg += Gray(img, x, y);
            n++;
        }

        // Filet vertical gauche, coupé à plusieurs hauteurs (loin des filets horizontaux).
        int xr = R(t.ColumnXs[0]);
        double top = t.RowYs[0], bottom = t.RowYs[^1];
        for (int k = 1; k <= 11; k++)
        {
            int y = R(top + (bottom - top) * k / 12);
            if (y < 0 || y >= img.Height || t.RowYs.Any(ry => Math.Abs(ry - y) < 6)) continue;
            int a = Math.Max(0, xr - 6), b = Math.Min(img.Width - 1, xr + 6);
            if (a >= b) continue;
            int run = Run(a, b, i => Gray(img, i, y), out int from, out int to);
            if (run == 0) continue;
            vRuns.Add(run);
            Take(Darkest(from, to, i => Gray(img, i, y)), y);
        }

        // Filets horizontaux, coupés dans la première colonne (loin des filets verticaux).
        double x0 = t.ColumnXs[0], x1 = t.ColumnXs[1];
        foreach (var ryD in t.RowYs)
        {
            int ry = R(ryD);
            for (int k = 1; k <= 5; k++)
            {
                int x = R(x0 + (x1 - x0) * k / 6);
                if (x < 0 || x >= img.Width || t.ColumnXs.Any(cx => Math.Abs(cx - x) < 6)) continue;
                int a = Math.Max(0, ry - 6), b = Math.Min(img.Height - 1, ry + 6);
                if (a >= b) continue;
                int run = Run(a, b, i => Gray(img, x, i), out int from, out int to);
                if (run == 0) continue;
                hRuns.Add(run);
                Take(x, Darkest(from, to, i => Gray(img, x, i)));
            }
        }

        int Median(List<int> v, int fallback)
        {
            if (v.Count == 0) return fallback;
            v.Sort();
            return Math.Clamp(v[v.Count / 2], 1, 8);
        }
        int vt = Median(vRuns, 2), ht = Median(hRuns, vt);
        byte[] color = n == 0 ? [40, 40, 40, 40] : [(byte)(s0 / n), (byte)(s1 / n), (byte)(s2 / n), (byte)(sg / n)];
        return (vt, ht, color);
    }

    private static int Darkest(int from, int to, Func<int, int> gray)
    {
        int at = from;
        for (int i = from; i <= to; i++) if (gray(i) < gray(at)) at = i;
        return at;
    }

    /// <summary>Trait sombre le plus marqué entre a et b : sa largeur (0 si aucun trait net).</summary>
    private static int Run(int a, int b, Func<int, int> gray, out int from, out int to)
    {
        int min = 256, at = a, max = 0;
        for (int i = a; i <= b; i++)
        {
            int g = gray(i);
            if (g < min) { min = g; at = i; }
            if (g > max) max = g;
        }
        from = to = at;
        if (min > 170 || max - min < 60) return 0;
        int thr = (min + max) / 2;
        while (from > a && gray(from - 1) < thr) from--;
        while (to < b && gray(to + 1) < thr) to++;
        return to - from + 1;
    }
}
