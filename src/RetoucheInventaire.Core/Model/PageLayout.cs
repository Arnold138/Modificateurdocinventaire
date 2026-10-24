using RetoucheInventaire.Core.Imaging;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Rangée qui s'agrandit : quand le texte d'une case passe sur plusieurs lignes (même taille d'écriture),
/// une bande est ajoutée sous la ligne et tout ce qui est en dessous descend d'autant.
/// Les positions de la page (cases, filets, rangées, zones verrouillées) sont décalées en conséquence ;
/// l'image est « étirée » : la bande reprend un interligne propre du scan (filets verticaux prolongés).
/// Entiers uniquement : même résultat sur Windows et sur iPhone/iPad.
/// </summary>
public static class PageLayout
{
    /// <summary>Hauteur de ligne du texte, en tailles de police (Times New Roman).</summary>
    public const double LineHeightRatio = 1.15;

    /// <summary>Somme des hauteurs ajoutées sur la page.</summary>
    public static int TotalExtra(Page page) => page.AllLines().Sum(l => l.Extra);

    /// <summary>Hauteur de la page redressée d'origine (sans les bandes ajoutées).</summary>
    public static int BaseHeight(Page page) => page.Height - TotalExtra(page);

    /// <summary>Case d'origine (sans la hauteur ajoutée).</summary>
    public static RectD BaseBox(PrintedLine line, Field f) => f.Box with { Height = f.Box.Height - line.Extra };

    /// <summary>
    /// Hauteur à ajouter pour que <paramref name="lines"/> lignes de texte en taille <paramref name="size"/> tiennent dans la case :
    /// la 1re ligne reste à la place du texte imprimé, les suivantes en dessous.
    /// </summary>
    public static int ExtraFor(PrintedLine line, Field f, int lines, double size)
    {
        if (lines <= 1) return 0;
        var b = BaseBox(line, f);
        double center = f.Ink.IsEmpty ? b.CenterY : f.Ink.CenterY;
        double top = Math.Clamp(center - 0.58 * size, b.Top, Math.Max(b.Top, b.Bottom - LineHeightRatio * size));
        double bottom = top + lines * LineHeightRatio * size + 2;
        return Math.Max(0, (int)Math.Ceiling(bottom - b.Bottom));
    }

    /// <summary>
    /// Fixe la hauteur ajoutée sous une ligne et décale tout ce qui est en dessous.
    /// Renvoie vrai si la page a changé (il faut redessiner).
    /// </summary>
    public static bool SetExtra(Page page, PrintedLine line, int extra)
    {
        extra = Math.Max(0, extra);
        if (extra == line.Extra) return false;
        if (line.Extra == 0) line.ExtraAt = CleanGap(page, line);
        int delta = extra - line.Extra;
        int at = line.ExtraAt;

        foreach (var t in page.Tables)
        {
            for (int i = 0; i < t.RowYs.Count; i++) if (t.RowYs[i] > at) t.RowYs[i] += delta;
            t.Bounds = Shift(t.Bounds, at, delta);
            foreach (var r in t.Rows) r.Bounds = Shift(r.Bounds, at, delta);
        }
        for (int i = 0; i < page.LockZones.Count; i++) page.LockZones[i] = Shift(page.LockZones[i], at, delta);
        foreach (var l in page.AllLines())
        {
            if (l == line)
            {
                foreach (var f in l.Cells) f.Box = f.Box with { Height = f.Box.Height + delta };
                continue;
            }
            if (l.Extra > 0 && l.ExtraAt > at) l.ExtraAt += delta;
            foreach (var f in l.Cells)
            {
                // Une ligne au-dessous (son centre d'origine est sous la bande) descend en entier.
                if (BaseBox(l, f).CenterY < at) continue;
                f.Box = f.Box with { Y = f.Box.Y + delta };
                if (!f.Ink.IsEmpty) f.Ink = f.Ink with { Y = f.Ink.Y + delta };
            }
        }
        page.Height += delta;
        line.Extra = extra;
        if (extra == 0) line.ExtraAt = 0;
        return true;
    }

    /// <summary>Rectangle : descend s'il est sous la bande, s'allonge s'il la traverse.</summary>
    private static RectD Shift(RectD r, int at, int delta)
    {
        if (r.Top >= at) return r with { Y = r.Y + delta };
        if (r.Bottom > at) return r with { Height = r.Height + delta };
        return r;
    }

    /// <summary>
    /// Ordonnée de la bande : au milieu de l'interligne sous le texte de la ligne (entre son encre et la ligne
    /// suivante de la rangée, ou le filet du bas), là où le scan ne contient que le papier et les filets verticaux.
    /// </summary>
    private static int CleanGap(Page page, PrintedLine line)
    {
        double a = line.Cells.Where(c => !c.Ink.IsEmpty).Select(c => c.Ink.Bottom).DefaultIfEmpty(line.Bounds.CenterY).Max();
        double b = line.Bounds.Bottom;
        foreach (var t in page.Tables)
            for (int r = 0; r < t.Rows.Count; r++)
            {
                int i = t.Rows[r].Lines.IndexOf(line);
                if (i < 0) continue;
                var below = t.Rows[r].Lines.Skip(i + 1).SelectMany(l => l.Cells).Where(c => !c.Ink.IsEmpty).Select(c => c.Ink.Top).ToList();
                b = below.Count > 0 ? below.Min() : (r + 1 < t.RowYs.Count ? t.RowYs[r + 1] - 4 : b);
            }
        return b > a ? (int)Math.Floor((a + b) / 2) : (int)Math.Floor(a);
    }

    /// <summary>
    /// Image de la page agrandie : l'image d'origine (hauteur <see cref="BaseHeight"/>) avec, pour chaque bande,
    /// la reprise de l'interligne propre sur la largeur du tableau (filets verticaux prolongés) et le papier ailleurs.
    /// </summary>
    public static Raster Stretch(Raster baseImage, Page page)
    {
        var bands = new List<(int At, int Height, int X0, int X1)>();
        foreach (var t in page.Tables)
            foreach (var l in t.Rows.SelectMany(r => r.Lines).Where(l => l.Extra > 0))
            {
                double left = t.HasGrid && t.ColumnXs.Count > 0 ? t.ColumnXs[0] : t.Bounds.Left;
                double right = t.HasGrid && t.ColumnXs.Count > 0 ? t.ColumnXs[^1] : t.Bounds.Right;
                bands.Add((l.ExtraAt, l.Extra, (int)Math.Floor(left) - 4, (int)Math.Ceiling(right) + 4));
            }
        if (bands.Count == 0) return baseImage;
        bands.Sort((x, y) => x.At.CompareTo(y.At));

        int w = baseImage.Width, ch = baseImage.Channels, stride = w * ch;
        int height = baseImage.Height + bands.Sum(b => b.Height);
        var data = new byte[height * stride];
        int src = 0, dst = 0, before = 0;
        foreach (var band in bands)
        {
            int s = Math.Clamp(band.At - before, 0, baseImage.Height - 1); // interligne, en coordonnées d'origine
            int n = s - src;
            if (n > 0) { Array.Copy(baseImage.Data, src * stride, data, dst * stride, n * stride); src += n; dst += n; }
            var paper = PaperOf(baseImage, s);
            int x0 = Math.Clamp(band.X0, 0, w), x1 = Math.Clamp(band.X1, 0, w);
            for (int k = 0; k < band.Height; k++, dst++)
                for (int x = 0; x < w; x++)
                {
                    int o = dst * stride + x * ch;
                    // Dans le tableau : les filets verticaux (sombres) sont prolongés, le reste devient papier propre.
                    if (x >= x0 && x < x1 && Gray(baseImage, x, s) < 150) Array.Copy(baseImage.Data, s * stride + x * ch, data, o, ch);
                    else Array.Copy(paper, 0, data, o, ch);
                }
            before += band.Height;
        }
        int rest = baseImage.Height - src;
        if (rest > 0) Array.Copy(baseImage.Data, src * stride, data, dst * stride, rest * stride);
        return new Raster(w, height, ch, data);
    }

    private static int Gray(Raster img, int x, int y)
    {
        int o = (y * img.Width + x) * img.Channels;
        return img.Channels == 1 ? img.Data[o] : (img.Data[o] * 29 + img.Data[o + 1] * 150 + img.Data[o + 2] * 77) >> 8;
    }

    /// <summary>Couleur du papier sur une rangée de pixels : moyenne de la moitié la plus claire.</summary>
    private static byte[] PaperOf(Raster img, int y)
    {
        int w = img.Width, ch = img.Channels;
        var lum = new List<(int L, int X)>(w);
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * ch;
            lum.Add((ch == 1 ? img.Data[o] : img.Data[o] + img.Data[o + 1] + img.Data[o + 2], x));
        }
        lum.Sort((a, b) => b.L != a.L ? b.L.CompareTo(a.L) : a.X.CompareTo(b.X));
        int n = Math.Max(1, w / 2);
        var sum = new long[ch];
        for (int i = 0; i < n; i++)
        {
            int o = (y * w + lum[i].X) * ch;
            for (int c = 0; c < ch; c++) sum[c] += img.Data[o + c];
        }
        var color = new byte[ch];
        for (int c = 0; c < ch; c++) color[c] = (byte)(sum[c] / n);
        return color;
    }
}
