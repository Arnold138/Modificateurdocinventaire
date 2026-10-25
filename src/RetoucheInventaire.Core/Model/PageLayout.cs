using RetoucheInventaire.Core.Imaging;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Rangée qui s'agrandit : quand le texte d'une case passe sur plusieurs lignes (même taille d'écriture),
/// une bande est ajoutée sous la ligne et tout ce qui est en dessous descend d'autant.
/// Les positions de la page (cases, filets, rangées, zones verrouillées) sont décalées en conséquence ;
/// l'image est « étirée » : la bande reprend un interligne propre du scan (filets verticaux prolongés).
/// Rangées ajoutées (ligne dupliquée, « Séparer ici ») : une bande insérée dans la page (<see cref="InsertedBand"/>),
/// tout ce qui est en dessous descend aussi ; voir <see cref="Rows"/>.
/// Entiers uniquement : même résultat sur Windows et sur iPhone/iPad.
/// </summary>
public static class PageLayout
{
    /// <summary>Hauteur de ligne du texte, en tailles de police (Times New Roman).</summary>
    public const double LineHeightRatio = 1.15;

    /// <summary>Somme des hauteurs ajoutées sur la page (rangées agrandies et rangées ajoutées).</summary>
    public static int TotalExtra(Page page) => page.AllLines().Sum(l => l.Extra) + page.Inserts.Sum(b => b.Height);

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
        ShiftBelow(page, line.ExtraAt, delta, line);
        foreach (var f in line.Cells) f.Box = f.Box with { Height = f.Box.Height + delta };
        line.Extra = extra;
        if (extra == 0) line.ExtraAt = 0;
        return true;
    }

    /// <summary>
    /// Décale de <paramref name="delta"/> tout ce qui est sous l'ordonnée <paramref name="at"/> (filets, rangées, cases, encre,
    /// zones verrouillées, autres bandes) et allonge la page. <paramref name="self"/> : ligne dont la bande change (traitée à part).
    /// </summary>
    public static void ShiftBelow(Page page, int at, int delta, PrintedLine? self = null)
    {
        foreach (var t in page.Tables)
        {
            for (int i = 0; i < t.RowYs.Count; i++) if (t.RowYs[i] > at) t.RowYs[i] += delta;
            t.Bounds = Shift(t.Bounds, at, delta);
            foreach (var r in t.Rows) r.Bounds = Shift(r.Bounds, at, delta);
        }
        for (int i = 0; i < page.LockZones.Count; i++) page.LockZones[i] = Shift(page.LockZones[i], at, delta);
        foreach (var l in page.AllLines())
        {
            if (l == self) continue;
            if (l.Extra > 0 && l.ExtraAt > at) l.ExtraAt += delta;
            foreach (var f in l.Cells)
            {
                // Une ligne au-dessous (son centre d'origine est sous la bande) descend en entier.
                if (BaseBox(l, f).CenterY < at) continue;
                f.Box = f.Box with { Y = f.Box.Y + delta };
                if (!f.Ink.IsEmpty) f.Ink = f.Ink with { Y = f.Ink.Y + delta };
            }
        }
        foreach (var b in page.Inserts) if (b.At >= at) b.At += delta;
        page.Height += delta;
    }

    /// <summary>
    /// Insère une bande à l'ordonnée <paramref name="at"/> : chaque morceau recopie des rangées entières de pixels
    /// de l'image d'origine. Tout ce qui est en dessous descend.
    /// </summary>
    public static InsertedBand InsertBand(Page page, int at, List<BandPart> parts, string kind)
    {
        int height = parts.Sum(p => p.Height);
        ShiftBelow(page, at, height);
        var band = new InsertedBand { Kind = kind, At = at, Height = height, Parts = parts };
        page.Inserts.Add(band);
        return band;
    }

    /// <summary>
    /// « Réduire la rangée » : retire les rangées de pixels [at, at + h[ (espace vide imprimé), ce qui est en dessous remonte.
    /// Bande « Trim » de hauteur négative (Skip : rangées de l'image d'origine sautées) ; RemoveBand la remet.
    /// </summary>
    public static InsertedBand CutBand(Page page, int at, int h, string? lineId = null)
    {
        ShiftBelow(page, at, -h);
        var band = new InsertedBand { Kind = "Trim", At = at, Height = -h, Skip = h, Line = lineId };
        page.Inserts.Add(band);
        return band;
    }

    /// <summary>Retire une bande insérée (son contenu a déjà été retiré) : ce qui est en dessous remonte.</summary>
    public static void RemoveBand(Page page, InsertedBand band)
    {
        page.Inserts.Remove(band);
        ShiftBelow(page, band.At, -band.Height);
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
    /// Morceau de la page agrandie : rangées de l'image d'origine (y compris celles recopiées par une bande insérée) ou bande
    /// d'interligne (<see cref="Extra"/>, ou <see cref="Gap"/> dans une bande insérée : filets verticaux de la rangée
    /// <see cref="Y0"/> prolongés entre X0 et X1, papier ailleurs).
    /// </summary>
    public sealed record Piece(int Y0, int N, int X0 = 0, int X1 = 0, bool Extra = false, bool Gap = false, int Span = 0, List<int>? Cols = null);

    /// <summary>Suite des rangées de la page agrandie, de haut en bas.</summary>
    public static List<Piece> Pieces(Page page) => Pieces(page, BaseHeight(page));

    public static List<Piece> Pieces(Page page, int baseHeight)
    {
        var extras = new List<(int At, int Height, int X0, int X1)>();
        foreach (var t in page.Tables)
            foreach (var l in t.Rows.SelectMany(r => r.Lines).Where(l => l.Extra > 0))
            {
                double left = t.HasGrid && t.ColumnXs.Count > 0 ? t.ColumnXs[0] : t.Bounds.Left;
                double right = t.HasGrid && t.ColumnXs.Count > 0 ? t.ColumnXs[^1] : t.Bounds.Right;
                extras.Add((l.ExtraAt, l.Extra, (int)Math.Floor(left) - 4, (int)Math.Ceiling(right) + 4));
            }
        extras = [.. extras.OrderBy(e => e.At)];
        // 1) Image d'origine et bandes insérées (positions sans les bandes d'interligne au-dessus d'elles).
        int Above(int y) => extras.Where(e => e.At < y).Sum(e => e.Height);
        var seq = new List<Piece>();
        int src = 0, inserted = 0;
        foreach (var b in page.Inserts.OrderBy(b => b.At))
        {
            int until = Math.Min(baseHeight, Math.Max(src, b.At - Above(b.At) - inserted));
            if (until > src) seq.Add(new Piece(src, until - src));
            src = until;
            if (b.Skip > 0) { src = Math.Min(baseHeight, until + b.Skip); inserted += b.Height; continue; } // rangée réduite : rangées sautées
            foreach (var p in b.Parts) if (p.Height > 0) seq.Add(p.Gap ? new Piece(p.Src, p.Height, p.X0, p.X1, Gap: true, Span: p.Span, Cols: p.Cols) : new Piece(p.Src, p.Height));
            inserted += b.Height;
        }
        if (baseHeight > src) seq.Add(new Piece(src, baseHeight - src));
        // 2) Bandes d'interligne, chacune avant la rangée qui suit sa position.
        int total = seq.Sum(x => x.N);
        int before = 0;
        foreach (var e in extras)
        {
            int q = Math.Min(Math.Max(0, total - 1), Math.Max(0, e.At - before));
            int pos = 0, s = 0, i = 0;
            Piece? from = null;
            for (; i < seq.Count; i++)
            {
                var x = seq[i];
                if (x.Extra) continue;
                if (q < pos + x.N)
                {
                    s = x.Gap ? x.Y0 : x.Y0 + (q - pos);
                    if (x.Gap) from = x;
                    if (q > pos)
                    {
                        seq[i] = x with { N = q - pos };
                        seq.Insert(i + 1, x with { Y0 = s, N = x.N - (q - pos) });
                        i++;
                    }
                    break;
                }
                pos += x.N;
            }
            // Dans l'interligne d'une rangée ajoutée : même rangée source.
            seq.Insert(i, new Piece(Math.Clamp(s, 0, Math.Max(0, baseHeight - 1)), e.Height, e.X0, e.X1, Extra: true, Span: from?.Span ?? 0, Cols: from?.Cols));
            before += e.Height;
        }
        return seq;
    }

    /// <summary>Rangée de l'image d'origine affichée à l'ordonnée <paramref name="y"/> de la page agrandie (null dans une bande d'interligne).</summary>
    public static int? ToBase(Page page, int y)
    {
        int pos = 0;
        foreach (var x in Pieces(page))
        {
            if (y < pos + x.N) return x.Extra || x.Gap ? null : x.Y0 + (y - pos);
            pos += x.N;
        }
        return null;
    }

    /// <summary>
    /// Image de la page agrandie : l'image d'origine (hauteur <see cref="BaseHeight"/>) avec, pour chaque bande d'interligne,
    /// la reprise de l'interligne propre sur la largeur du tableau (filets verticaux prolongés) et le papier ailleurs,
    /// et les rangées ajoutées (rangées de pixels recopiées).
    /// </summary>
    public static Raster Stretch(Raster baseImage, Page page)
    {
        if (TotalExtra(page) == 0 && page.Inserts.Count == 0) return baseImage;
        var seq = Pieces(page, baseImage.Height);
        int w = baseImage.Width, ch = baseImage.Channels, stride = w * ch;
        int height = seq.Sum(x => x.N);
        var data = new byte[height * stride];
        int dst = 0;
        foreach (var x in seq)
        {
            if (x.Extra || x.Gap)
            {
                int s = x.Span > 0 ? CleanRow(baseImage, x.Y0, x.Span, x.Cols ?? []) : x.Y0;
                var paper = PaperOf(baseImage, s);
                int x0 = Math.Clamp(x.X0, 0, w), x1 = Math.Clamp(x.X1, 0, w);
                for (int k = 0; k < x.N; k++, dst++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        int o = dst * stride + xx * ch;
                        // Dans le tableau : les filets verticaux (sombres) sont prolongés, le reste devient papier propre.
                        if (xx >= x0 && xx < x1 && Gray(baseImage, xx, s) < 150) Array.Copy(baseImage.Data, s * stride + xx * ch, data, o, ch);
                        else Array.Copy(paper, 0, data, o, ch);
                    }
                continue;
            }
            int a = Math.Clamp(x.Y0, 0, baseImage.Height), b = Math.Min(baseImage.Height, a + x.N);
            if (b > a) Array.Copy(baseImage.Data, a * stride, data, dst * stride, (b - a) * stride);
            dst += x.N;
        }
        return new Raster(w, height, ch, data);
    }

    /// <summary>
    /// Rangée la plus nette pour prolonger les filets verticaux, parmi [s0, s0 + span[ : le plus de pixels sombres près des
    /// filets du tableau (± 3 px) et le moins ailleurs (texte). À égalité, la plus haute.
    /// </summary>
    public static int CleanRow(Raster img, int s0, int span, List<int> cols)
    {
        int w = img.Width;
        var near = new bool[w];
        foreach (var c in cols) for (int x = Math.Max(0, c - 3); x <= Math.Min(w - 1, c + 3); x++) near[x] = true;
        int best = Math.Clamp(s0, 0, img.Height - 1), bestScore = int.MinValue;
        for (int y = Math.Max(0, s0); y < Math.Min(img.Height, s0 + span); y++)
        {
            int score = 0;
            for (int x = 0; x < w; x++) if (Gray(img, x, y) < 150) score += near[x] ? 1 : -1;
            if (score > bestScore) { bestScore = score; best = y; }
        }
        return best;
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
