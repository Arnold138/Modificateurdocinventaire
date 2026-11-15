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
    /// <summary>« Ligne plus haute » (Tall : lignes d'écriture en plus, page ajoutée) : hauteur à ajouter sous la ligne.</summary>
    public static int TallExtra(PrintedLine line)
    {
        if (line.Tall <= 0 || line.Cells.Count == 0) return 0;
        var f = line.Cells[0];
        return ExtraFor(line, f, 1 + line.Tall, f.FontSize > 0 ? f.FontSize : 12);
    }

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
        int hi = int.MaxValue;
        foreach (var t in page.Tables)
            for (int r = 0; r < t.Rows.Count; r++)
            {
                int i = t.Rows[r].Lines.IndexOf(line);
                if (i < 0) continue;
                var below = t.Rows[r].Lines.Skip(i + 1).SelectMany(l => l.Cells).Where(c => !c.Ink.IsEmpty).Select(c => c.Ink.Top).ToList();
                b = below.Count > 0 ? below.Min() : (r + 1 < t.RowYs.Count ? t.RowYs[r + 1] - 4 : b);
                if (r + 1 < t.RowYs.Count) hi = (int)Math.Floor(t.RowYs[r + 1]) - 3;
            }
        // Toujours dans la rangée de la ligne (au-dessus de son filet du bas) : sinon c'est la rangée suivante qui grandirait.
        return Math.Min(hi, b > a ? (int)Math.Floor((a + b) / 2) : (int)Math.Floor(a));
    }

    /// <summary>
    /// Morceau de la page agrandie : rangées de l'image d'origine (y compris celles recopiées par une bande insérée) ou bande
    /// d'interligne (<see cref="Extra"/>, ou <see cref="Gap"/> dans une bande insérée : filets verticaux de la rangée
    /// <see cref="Y0"/> prolongés entre X0 et X1, papier ailleurs).
    /// </summary>
    public sealed record Piece(int Y0, int N, int X0 = 0, int X1 = 0, bool Extra = false, bool Gap = false, int Span = 0, List<int>? Cols = null, bool Straight = false, int Anchor = -1);

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
            foreach (var p in b.Parts) if (p.Height > 0) seq.Add(p.Gap ? new Piece(p.Src, p.Height, p.X0, p.X1, Gap: true, Span: p.Span, Cols: p.Cols, Anchor: until) : new Piece(p.Src, p.Height, Straight: p.Straight, Anchor: until));
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
            int sc = Math.Clamp(s, 0, Math.Max(0, baseHeight - 1));
            seq.Insert(i, new Piece(sc, e.Height, e.X0, e.X1, Extra: true, Span: from?.Span ?? 0, Cols: from?.Cols, Anchor: from?.Anchor ?? sc));
            before += e.Height;
        }
        return seq;
    }

    /// <summary>Rangée de l'image d'origine affichée à l'ordonnée <paramref name="y"/> de la page agrandie (null dans une bande d'interligne).</summary>
    /// <summary>Rangées de l'image d'origine → ordonnée (la 1re) où chacune est affichée sur la page agrandie.</summary>
    public static Dictionary<int, int> BaseToPage(Page page)
    {
        var map = new Dictionary<int, int>();
        int pos = 0;
        foreach (var x in Pieces(page))
        {
            if (!x.Extra && !x.Gap) for (int i = 0; i < x.N; i++) map.TryAdd(x.Y0 + i, pos + i);
            pos += x.N;
        }
        return map;
    }

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
        var profiles = new Dictionary<int, (byte[] Ink, bool[] Has)>();
        foreach (var x in seq)
        {
            if (x.Extra || x.Gap)
            {
                int s = x.Span > 0 ? CleanRow(baseImage, x.Y0, x.Span, x.Cols ?? []) : x.Y0;
                var paper = PaperOf(baseImage, s);
                int anchor = Math.Clamp(x.Anchor >= 0 ? x.Anchor : x.Y0, 0, baseImage.Height - 1);
                // Filets verticaux tels qu'ils sont sur la page À L'ENDROIT de l'ajout (rangées voisines) : même place au pixel
                // près, même épaisseur, même encre ; une écriture qui touche un filet n'est pas recopiée.
                if (!profiles.TryGetValue(anchor, out var prof)) profiles[anchor] = prof = RuleProfile(baseImage, anchor, Gray(paper));
                var missing = MissingRules(prof.Has, x.Cols, Math.Clamp(x.X0, 0, w), Math.Clamp(x.X1, 0, w));
                for (int k = 0; k < x.N; k++, dst++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        int o = dst * stride + xx * ch;
                        if (prof.Has[xx]) Array.Copy(prof.Ink, xx * ch, data, o, ch);
                        else if (missing != null && missing[xx] >= 0) Array.Copy(prof.Ink, missing[xx] * ch, data, o, ch);
                        else if (missing != null && missing[xx] == -2) for (int c = 0; c < ch; c++) data[o + c] = c == 3 ? (byte)255 : RuleInk;
                        else Array.Copy(paper, 0, data, o, ch);
                    }
                continue;
            }
            if (x.Straight)
            {
                if (x.Anchor >= 0) TableRule(baseImage, x.Y0, x.Anchor, x.N, data, dst);
                else StraightRule(baseImage, x.Y0, x.N, data, dst);
                dst += x.N;
                continue;
            }
            int a = Math.Clamp(x.Y0, 0, baseImage.Height), b = Math.Min(baseImage.Height, a + x.N);
            if (b > a) Array.Copy(baseImage.Data, a * stride, data, dst * stride, (b - a) * stride);
            dst += x.N;
        }
        return new Raster(w, height, ch, data);
    }

    /// <summary>Gris d'un filet redessiné.</summary>
    internal const byte RuleInk = 70;

    /// <summary>
    /// Filets verticaux de la page autour de la rangée <paramref name="anchor"/> (± 64 rangées, sans les filets horizontaux) :
    /// une abscisse est un filet si elle est encrée sur au moins les 3/4 de ces rangées (une écriture, même à cheval sur un
    /// filet, n'y est pas) ; son encre est celle de la rangée médiane. Donne aussi le bord gauche ou droit d'un tableau qui
    /// n'est pas une colonne lue.
    /// </summary>
    internal static (byte[] Ink, bool[] Has) RuleProfile(Raster img, int anchor, int paperGray)
    {
        int w = img.Width, ch = img.Channels, H = img.Height;
        int a = Math.Max(0, anchor - 64), b = Math.Min(H, anchor + 64);
        var rule = new bool[b - a];
        for (int y = a; y < b; y++)
        {
            int dark = 0;
            for (int x = 0; x < w; x++) if (Gray(img, x, y) < 150) dark++;
            rule[y - a] = dark * 3 > w;
        }
        var rows = new List<int>();
        for (int y = a; y < b; y++)
        {
            bool near = false;
            for (int d = -3; d <= 3 && !near; d++) near = y + d >= a && y + d < b && rule[y + d - a];
            if (!near) rows.Add(y);
        }
        if (rows.Count < 8) { rows.Clear(); for (int y = a; y < b; y++) rows.Add(y); }
        var ink = new byte[w * ch];
        var has = new bool[w];
        int n = rows.Count;
        var g = new (int G, int Y)[n];
        for (int x = 0; x < w; x++)
        {
            for (int i = 0; i < n; i++) g[i] = (Gray(img, x, rows[i]), rows[i]);
            Array.Sort(g, (p, q) => p.G != q.G ? p.G.CompareTo(q.G) : p.Y.CompareTo(q.Y));
            has[x] = g[n * 3 / 4].G < paperGray - 60;
            Array.Copy(img.Data, (g[n / 2].Y * w + x) * ch, ink, x * ch, ch);
        }
        return (ink, has);
    }

    /// <summary>
    /// Filets du tableau (<paramref name="cols"/>) absents à cet endroit du scan (effacés) : redessinés avec le trait du
    /// filet vertical trouvé le plus proche (même épaisseur, même encre), centré sur la colonne. Pour chaque pixel :
    /// l'abscisse du trait à recopier, -2 (gris par défaut, aucun filet trouvé), -1 ailleurs ; null si rien ne manque.
    /// </summary>
    internal static int[]? MissingRules(bool[] has, List<int>? cols, int x0, int x1)
    {
        if (cols is null || cols.Count == 0) return null;
        int w = has.Length;
        int[]? missing = null;
        foreach (int c in cols)
        {
            if (c < x0 || c >= x1) continue;
            bool there = false;
            for (int xx = Math.Max(0, c - 3); xx <= Math.Min(w - 1, c + 3) && !there; xx++) there = has[xx];
            if (there) continue;
            if (missing is null) { missing = new int[w]; Array.Fill(missing, -1); }
            // Trait le plus proche : une suite de pixels de filet autour d'une autre colonne du tableau.
            int r0 = -1, r1 = -1, bestD = int.MaxValue;
            foreach (int o in cols)
            {
                int d = Math.Abs(o - c), k = -1;
                if (o == c || d >= bestD) continue;
                for (int xx = Math.Max(0, o - 3); xx <= Math.Min(w - 1, o + 3) && k < 0; xx++) if (has[xx]) k = xx;
                if (k < 0) continue;
                int e = k;
                while (e + 1 < w && has[e + 1] && e - k < 8) e++;
                r0 = k; r1 = e; bestD = d;
            }
            if (r0 < 0) { for (int xx = Math.Max(0, c - 1); xx <= Math.Min(w - 1, c); xx++) missing[xx] = -2; continue; }
            int len = r1 - r0 + 1, start = c - len / 2;
            for (int i = 0; i < len; i++) if (start + i >= 0 && start + i < w) missing[start + i] = r0 + i;
        }
        return missing;
    }

    /// <summary>
    /// Filet du haut d'une rangée ajoutée : le filet de la page à l'endroit de l'ajout (celui qui sépare les deux rangées
    /// voisines), redressé : même trait que ses voisins (épaisseur, noir, bords du tableau au pixel près). Là où ce filet
    /// est croisé par une écriture (encre en trop, couleur) ou effacé, la colonne vient du filet de la rangée recopiée
    /// (<paramref name="src"/>). Sans filet à cet endroit (trait tracé au milieu d'une rangée) : celui de la rangée recopiée.
    /// </summary>
    internal static void TableRule(Raster img, int src, int anchor, int n, byte[] data, int dst)
    {
        int w = img.Width, ch = img.Channels, stride = w * ch, H = img.Height;
        bool ruleHere = false;
        for (int y = Math.Max(0, anchor - 2); y < Math.Min(H, anchor + n + 2) && !ruleHere; y++)
        {
            int run = 0, best = 0, gap = 0;
            for (int x = 0; x < w; x++)
            {
                if (Gray(img, x, y) < 150) { run += gap + 1; gap = 0; best = Math.Max(best, run); }
                else if (run > 0 && ++gap > 2) { run = 0; gap = 0; }
            }
            ruleHere = best * 4 > w;
        }
        if (!ruleHere || anchor == src) { StraightRule(img, src, n, data, dst); return; }
        var here = new byte[n * stride];
        var model = new byte[n * stride];
        StraightRule(img, anchor, n, here, 0);
        StraightRule(img, src, n, model, 0);
        var badHere = BadColumns(here, w, ch, n);
        var badModel = BadColumns(model, w, ch, n);
        for (int x = 0; x < w; x++)
        {
            var from = badHere[x] && !badModel[x] ? model : here;
            for (int k = 0; k < n; k++) Array.Copy(from, k * stride + x * ch, data, (dst + k) * stride + x * ch, ch);
        }
    }

    /// <summary>
    /// Colonnes d'un filet redressé (n rangées) où il n'a pas l'air d'un filet : beaucoup plus d'encre que d'habitude
    /// (écriture qui le croise), presque pas d'encre (effacé), ou de la couleur.
    /// </summary>
    private static bool[] BadColumns(byte[] band, int w, int ch, int n)
    {
        var ink = new int[w];
        var colored = new bool[w];
        for (int x = 0; x < w; x++)
            for (int k = 0; k < n; k++)
            {
                int o = (k * w + x) * ch;
                ink[x] += 255 - Gray(band, o, ch);
                if (ch >= 3 && Math.Max(band[o], Math.Max(band[o + 1], band[o + 2])) - Math.Min(band[o], Math.Min(band[o + 1], band[o + 2])) > 60) colored[x] = true;
            }
        var sorted = (int[])ink.Clone();
        Array.Sort(sorted);
        int typical = sorted[w / 2];
        var bad = new bool[w];
        for (int x = 0; x < w; x++) bad[x] = colored[x] || ink[x] * 10 > typical * 16 + 1500 || ink[x] * 2 < typical;
        return bad;
    }

    private static int Gray(byte[] d, int o, int ch) => ch == 1 ? d[o] : (d[o] * 29 + d[o + 1] * 150 + d[o + 2] * 77) >> 8;

    /// <summary>
    /// Filet redressé : pour chaque abscisse, la rangée la plus sombre autour de [<paramref name="y0"/>, y0 + n[ (± 4 px),
    /// lissée par blocs de 32 px (une lettre qui touche le filet ne le fait pas sauter) ; les n rangées écrites suivent ce
    /// filet. Un scan un peu penché donne ainsi un filet droit et entier.
    /// </summary>
    internal static void StraightRule(Raster img, int y0, int n, byte[] data, int dst)
    {
        int w = img.Width, ch = img.Channels, stride = w * ch, H = img.Height;
        int a = Math.Max(0, y0 - 4), b = Math.Min(H, y0 + n + 4);
        if (b <= a) return;
        var yd = new int[w];
        var ok = new bool[w];
        for (int xx = 0; xx < w; xx++)
        {
            int best = 256, by = -1;
            for (int y = a; y < b; y++) { int g = Gray(img, xx, y); if (g < best) { best = g; by = y; } }
            yd[xx] = by; ok[xx] = best < 160;
        }
        const int Block = 32;
        int nb = (w + Block - 1) / Block;
        var mid = new double[nb];
        var has = new bool[nb];
        for (int k = 0; k < nb; k++)
        {
            var v = new List<int>();
            for (int xx = k * Block; xx < Math.Min(w, (k + 1) * Block); xx++) if (ok[xx]) v.Add(yd[xx]);
            if (v.Count >= Block / 4) { v.Sort(); mid[k] = v[v.Count / 2]; has[k] = true; }
        }
        double center = y0 + (n - 1) / 2.0;
        if (!has.Any(x => x))
        {
            for (int k = 0; k < n; k++) Array.Copy(img.Data, Math.Clamp(y0 + k, 0, H - 1) * stride, data, (dst + k) * stride, stride);
            return;
        }
        // Blocs sans filet : la valeur du bloc le plus proche qui en a un.
        for (int k = 0; k < nb; k++)
            if (!has[k])
            {
                int d = 1;
                while (true)
                {
                    if (k - d >= 0 && has[k - d]) { mid[k] = mid[k - d]; break; }
                    if (k + d < nb && has[k + d]) { mid[k] = mid[k + d]; break; }
                    d++;
                }
            }
        // Hauteur de référence : la médiane du filet. Un filet droit est recopié tel quel (décalage 0 partout) ; un filet
        // penché est ramené à cette hauteur colonne par colonne ; s'il est loin du milieu de la bande, il y est recentré.
        var sorted = mid.OrderBy(v => v).ToArray();
        double reference = sorted[sorted.Length / 2];
        int off = Math.Abs(reference - center) >= 2 ? (int)Math.Round(reference - center) : 0;
        // Petit trou dans le filet (tache de correcteur, encre pâle) : recopié depuis la colonne voisine la plus proche
        // où le filet est là (jusqu'à 150 px de chaque côté).
        var from = new int[w];
        int lastOk = -100000;
        for (int xx = 0; xx < w; xx++) { if (ok[xx]) lastOk = xx; from[xx] = lastOk; }
        int nextOk = 100000;
        for (int xx = w - 1; xx >= 0; xx--)
        {
            if (ok[xx]) { nextOk = xx; from[xx] = xx; continue; }
            int l = from[xx];
            from[xx] = xx - l <= 150 && nextOk - xx <= 150 ? (xx - l <= nextOk - xx ? l : nextOk) : xx;
        }
        for (int xx = 0; xx < w; xx++)
        {
            int sx = from[xx];
            // Interpolation entre centres de blocs.
            double f = (xx + 0.5) / Block - 0.5;
            int k0 = Math.Clamp((int)Math.Floor(f), 0, nb - 1), k1 = Math.Min(nb - 1, k0 + 1);
            double t = Math.Clamp(f - k0, 0, 1);
            double line = mid[k0] * (1 - t) + mid[k1] * t;
            int shift = (int)Math.Round(line - reference) + off;
            for (int k = 0; k < n; k++)
            {
                // Colonne bouchée : le filet de la voisine, centré sur sa rangée la plus sombre.
                int sy = Math.Clamp(sx != xx ? y0 + k + (int)Math.Round(yd[sx] - reference) + off : y0 + k + shift, 0, H - 1);
                Array.Copy(img.Data, sy * stride + sx * ch, data, (dst + k) * stride + xx * ch, ch);
            }
        }
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

    internal static int Gray(byte[] px) => px.Length == 1 ? px[0] : (px[0] * 29 + px[1] * 150 + px[2] * 77) >> 8;

    internal static int Gray(Raster img, int x, int y)
    {
        int o = (y * img.Width + x) * img.Channels;
        return img.Channels == 1 ? img.Data[o] : (img.Data[o] * 29 + img.Data[o + 1] * 150 + img.Data[o + 2] * 77) >> 8;
    }

    /// <summary>Couleur du papier sur une rangée de pixels : moyenne de la moitié la plus claire.</summary>
    internal static byte[] PaperOf(Raster img, int y)
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
