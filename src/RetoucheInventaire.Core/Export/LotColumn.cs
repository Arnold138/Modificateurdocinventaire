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
    /// <summary>Un tableau à encadrer et ce qu'il faut tracer (rangées encadrées, dans l'ordre d'affichage).</summary>
    public sealed record Frame(Table Table, int Left, int Right, List<int> Rows, int VThick, int HThick, byte[] Color);

    private static int R(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>Tableaux à grille qui portent au moins un numéro de lot.</summary>
    public static List<Table> FramedTables(Page page) =>
        page.Tables.Where(t => t.HasGrid && t.ColumnXs.Count >= 2 && t.RowYs.Count >= 2
                               && t.Rows.SelectMany(r => r.Lines).Any(Lots.HasCell)).ToList();

    /// <summary>
    /// Rangées où la colonne est tracée : de la 1re à la dernière rangée qui porte une case de lot (ordre d'affichage),
    /// plus l'en-tête juste au-dessus. Rien sous le dernier objet (totaux, rangées vides).
    /// </summary>
    public static List<int> FramedRows(Table table)
    {
        var order = Lots.RowOrder(table).Where(r => r + 1 < table.RowYs.Count).ToList();
        var has = order.Select(r => table.Rows[r].Lines.Any(Lots.HasCell)).ToList();
        int first = has.IndexOf(true), last = has.LastIndexOf(true);
        if (first < 0) return [];
        int from = first;
        while (from > 0 && table.Rows[order[from - 1]].Lines.Count > 0 && table.Rows[order[from - 1]].Lines.All(l => l.Kind == LineKind.EnTete)) from--;
        return order.GetRange(from, last + 1 - from);
    }

    /// <summary>Signature de ce qui est tracé (pour savoir s'il faut redessiner l'écran).</summary>
    public static string Signature(Page page, bool enabled) =>
        !enabled ? "" : string.Join(",", FramedTables(page).Select(t => page.Tables.IndexOf(t) + ":" + string.Join(".", FramedRows(t)))) + "/" + Lots.ColumnWidth(page);

    public static List<Frame> Frames(Raster image, Page page)
    {
        double w = Lots.ColumnWidth(page);
        var frames = new List<Frame>();
        foreach (var t in FramedTables(page))
        {
            int right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
            if (right - left < 8) continue;
            var rows = FramedRows(t);
            if (rows.Count == 0) continue;
            // Traits mesurés une fois, sur l'imprimé (gardés dans le projet) : la colonne garde le même style quoi qu'on ajoute.
            if (t.Rules is null) { var (v, h, c) = MeasureRules(image, t, page); t.Rules = new RuleStyle { V = v, H = h, Color = c.Select(b => (int)b).ToList() }; }
            frames.Add(new Frame(t, left, right, rows, t.Rules.V, t.Rules.H, t.Rules.Color.Select(b => (byte)b).ToArray()));
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
        {
            var rows = FramedRows(t);
            if (rows.Count == 0) continue;
            double top = rows.Min(r => t.RowYs[r]), bottom = rows.Max(r => t.RowYs[r + 1]);
            boxes.Add(RectD.FromEdges(t.ColumnXs[0] - w - 8, top - 8, t.ColumnXs[0] - 1, bottom + 8));
        }
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
            int half = f.VThick / 2, hh = f.HThick / 2;
            // Chaque rangée porte ses traits (gauche, haut, bas) : elle peut être déplacée avec eux.
            foreach (var r in f.Rows)
            {
                int y0 = R(f.Table.RowYs[r]), y1 = R(f.Table.RowYs[r + 1]);
                Fill(output, f.Left - half, y0 - hh, f.VThick, y1 - y0 + f.HThick, f.Color);
                // Trait entre le numéro et la désignation (trait gauche du tableau) : partout où l'image ne l'a pas (rangée
                // ajoutée, trait imprimé qui s'arrête ou décalé), il est tracé, ligne de pixels par ligne de pixels.
                for (int y = Math.Max(0, y0 - hh); y < Math.Min(output.Height, y1 - hh + f.HThick); y++)
                {
                    bool dark = false;
                    for (int x = Math.Max(0, f.Right - 8); x <= Math.Min(output.Width - 1, f.Right + 3) && !dark; x++) dark = Gray(output, x, y) < 150;
                    if (!dark) Fill(output, f.Right - f.VThick, y, f.VThick, 1, f.Color); // dans la colonne, contre le tableau
                }
                Fill(output, f.Left - half, y0 - hh, f.Right - (f.Left - half), f.HThick, f.Color);
                Fill(output, f.Left - half, y1 - hh, f.Right - (f.Left - half), f.HThick, f.Color);
            }
        }
        return output;
    }

    /// <summary>
    /// Fond de chaque case de la colonne : le même que celui de la rangée (gris hachuré de l'en-tête, papier ailleurs),
    /// recopié depuis le morceau le plus clair de la 1re colonne (jamais du texte, même si l'OCR ne l'a pas lu).
    /// </summary>
    /// <summary>
    /// Trait gauche du tableau dans la rangée [y0, y1[ : abscisse de son premier pixel sombre près de <paramref name="right"/>
    /// (il peut être un peu décalé : page légèrement de travers), mesurée sur trois hauteurs ; null s'il n'y est pas.
    /// </summary>
    private static int? RuleLeftOf(Raster img, int right, int y0, int y1)
    {
        int? found = null;
        foreach (int y in new[] { y0 + 2, (y0 + y1) >> 1, y1 - 3 })
        {
            if (y < y0 || y >= y1 || y < 0 || y >= img.Height) continue;
            for (int x = Math.Max(0, right - 8); x <= Math.Min(img.Width - 1, right + 3); x++)
                if (Gray(img, x, y) < 150) { found = found is int a ? Math.Min(a, x) : x; break; }
        }
        return found;
    }

    private static void DrawBackgrounds(Raster source, Raster output, Frame f)
    {
        var t = f.Table;
        int x0 = f.Left - f.VThick / 2 + f.VThick, full = f.Right - f.VThick / 2;   // intérieur de la colonne
        if (full - x0 <= 0) return;
        foreach (var r in f.Rows)
        {
            int y0 = R(t.RowYs[r]) - f.HThick / 2 + f.HThick, y1 = R(t.RowYs[r + 1]) - f.HThick / 2;
            if (y1 <= y0) continue;
            // Le fond s'arrête avant le trait gauche du tableau, là où il est sur l'image (il reste visible).
            int? rule = RuleLeftOf(source, f.Right, y0, y1);
            int x1 = rule is int rl ? Math.Min(full, rl) : full;
            int w = x1 - x0;
            if (w <= 0) continue;
            var ink = (r < t.Rows.Count ? t.Rows[r].Lines : [])
                .SelectMany(l => l.Cells).Where(c => !c.Ink.IsEmpty).Select(c => c.Ink).ToList();
            int start = R(t.ColumnXs[0]) + f.VThick + 2, end = R(t.ColumnXs[1]) - f.VThick - 2 - w;
            int best = -1;
            long bestSum = -1;
            for (int sx = start; sx <= end; sx += 2)
            {
                var patch = new RectD(sx - 3, y0, w + 6, y1 - y0);
                if (ink.Any(i => i.IntersectsWith(patch))) continue;
                long sum = 0;
                for (int y = y0; y < y1; y += 2)
                    for (int x = sx; x < sx + w; x += 2)
                        if (x >= 0 && x < source.Width && y >= 0 && y < source.Height) sum += Gray(source, x, y);
                if (sum > bestSum) { bestSum = sum; best = sx; }
            }
            if (best >= 0) Copy(source, output, best, y0, x0, y0, w, y1 - y0);
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
    /// <summary>
    /// <paramref name="page"/> : les traits sont mesurés seulement sur les rangées imprimées (pas dans une rangée ajoutée ni une
    /// bande d'agrandissement, où le trait recopié est plus fin) : l'épaisseur ne change pas quand on ajoute des lignes.
    /// </summary>
    public static (int VThick, int HThick, byte[] Color) MeasureRules(Raster img, Table t, Page? page = null)
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
        // Mesures aux mêmes endroits de l'image imprimée, quoi qu'on ajoute (positions prises sur la page d'origine).
        var map = page != null && (page.Inserts.Count > 0 || page.Tables.Any(u => u.Rows.Any(r => r.Lines.Any(l => l.Extra > 0)))) ? PageLayout.BaseToPage(page) : null;
        if (map != null)
        {
            top += (PageLayout.ToBase(page!, R(top)) ?? R(top)) - R(top);
            bottom += (PageLayout.ToBase(page!, R(bottom)) ?? R(bottom)) - R(bottom);
        }
        for (int k = 1; k <= 11; k++)
        {
            int yb = R(top + (bottom - top) * k / 12);
            int y = yb;
            if (map != null && !map.TryGetValue(yb, out y)) continue;
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
        for (int ri = 0; ri < t.RowYs.Count; ri++)
        {
            if (page != null && ri < t.Rows.Count && t.Rows[ri].Inserted != null) continue; // filet recopié d'une rangée ajoutée : pas mesuré
            int ry = R(t.RowYs[ri]);
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
