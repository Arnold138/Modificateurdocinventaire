using RetoucheInventaire.Core.Export;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Lignes ajoutées (menu « Ligne », clic droit) : de vraies rangées du tableau, insérées dans la page.
/// <list type="bullet">
/// <item>« Dupliquer la ligne » : une rangée est ajoutée juste sous celle de la ligne, avec une copie de la ligne
/// (texte et valeurs, pixels recopiés : elle ressemble à l'imprimé) ; on corrige ce qui change (quantité, prix).
/// Ses montants comptent dans les totaux (ils ne sont pas dans le total imprimé). Case de lot vide.</item>
/// <item>« Séparer ici » : la ligne touchée (et celles qui la suivent dans la rangée) devient une rangée à part :
/// un filet est tracé au-dessus d'elle (recopié du filet de la rangée), avec sa case de lot et ses cases de prix.</item>
/// <item>Texte tapé sur plusieurs lignes dans la désignation : « Séparer ici » envoie la suite du texte dans une
/// rangée ajoutée juste en dessous (rangée vierge, filets du tableau, prix à taper).</item>
/// </list>
/// Tout ce qui est en dessous descend ; rien d'autre ne change (numéros de lot, autres cases, mise en page).
/// « Supprimer la ligne ajoutée » / « Remettre dans la rangée du dessus » défont exactement l'opération.
/// Portage identique dans web/core/rows.js.
/// </summary>
public static class Rows
{
    public sealed record Location(Table Table, int R, int I, Row Row);

    private static bool GridTable(Table t) => t.HasGrid && t.RowYs.Count >= 2 && t.ColumnXs.Count >= 2;

    /// <summary>Tableau, rangée et rang de la ligne dans sa rangée (null hors tableau à filets).</summary>
    public static Location? Locate(Page page, PrintedLine line)
    {
        foreach (var t in page.Tables)
        {
            if (!GridTable(t)) continue;
            for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
            {
                int i = t.Rows[r].Lines.IndexOf(line);
                if (i >= 0) return new Location(t, r, i, t.Rows[r]);
            }
        }
        return null;
    }

    /// <summary>Bande insérée qui a créé cette rangée (null pour une rangée imprimée).</summary>
    public static InsertedBand? BandOf(Page page, Row row) =>
        row.Inserted is null ? null : page.Inserts.FirstOrDefault(b => b.Id == row.Inserted);

    /// <summary>Une ligne d'article (ou un titre) d'un tableau à filets peut être dupliquée ; pas un total ni un en-tête.</summary>
    /// <summary>Ligne d'un tableau où l'on peut ajouter des lignes vides (toute ligne du tableau, même vide, un total ou un titre).</summary>
    public static bool CanAddRows(Page page, PrintedLine line) =>
        !line.OutsideTable && line.Kind != LineKind.Ignoree && !line.Deleted && Locate(page, line) != null;

    /// <summary>Au-dessus : partout sauf au-dessus de l'en-tête du tableau.</summary>
    public static bool CanAddAbove(Page page, PrintedLine line) =>
        Locate(page, line) is { } at && (at.R > 0 || !at.Row.Lines.Any(l => l.Kind == LineKind.EnTete));

    public static bool CanCopy(Page page, PrintedLine line) =>
        !line.OutsideTable && (line.Kind is LineKind.Article or LineKind.Titre) && (line.FirstText.Length > 0 || page.Added != null) && Locate(page, line) != null;

    /// <summary>
    /// Le trait tracé au-dessus de la ligne i passe bien entre les lignes : toutes les cases d'avant au-dessus, toutes celles
    /// d'après en dessous (une lecture parasite sur un fond tramé peut chevaucher la ligne d'au-dessus : pas de trait alors).
    /// </summary>
    private static bool SplitOk(Row row, int i)
    {
        if (i <= 0 || i >= row.Lines.Count) return false;
        double y = Arrangement.GapMid(row.Lines[i - 1], row.Lines[i]) + Arrangement.Cut;
        static double Cy(PrintedLine l, Field f) { var b = PageLayout.BaseBox(l, f); return b.Y + b.Height / 2; }
        return row.Lines.Select((l, k) => (l, k)).All(x => x.l.Cells.All(f => x.k < i ? Cy(x.l, f) < y : Cy(x.l, f) > y));
    }

    /// <summary>« Séparer ici » : la ligne n'est pas la première de sa rangée, et le trait passe bien entre les lignes.</summary>
    public static bool CanSplit(Page page, PrintedLine line) =>
        !line.OutsideTable && Locate(page, line) is { } at && SplitOk(at.Row, at.I);

    /// <summary>Rangée ajoutée par l'utilisateur qui contient cette ligne (null sinon).</summary>
    public static (Location At, InsertedBand Band)? AddedRowOf(Page page, PrintedLine line)
    {
        var at = Locate(page, line);
        var band = at is null ? null : BandOf(page, at.Row);
        return band is null ? null : (at!, band);
    }

    /// <summary>Rangées de pixels (page agrandie) [y0, y1[ → morceaux de l'image d'origine.</summary>
    /// <summary>
    /// Rangées de pixels (page agrandie) [y0, y1[ → morceaux : rangées de l'image d'origine, et interlignes d'une rangée déjà
    /// ajoutée (recopiés comme interlignes : sans eux, copier une ligne ajoutée donnait une rangée réduite à son filet).
    /// Les bandes d'agrandissement d'un texte tapé ne sont pas recopiées (la copie grandit à son tour si son texte le demande).
    /// </summary>
    private static List<BandPart> PartsOf(Page page, int y0, int y1)
    {
        var seq = PageLayout.Pieces(page);
        var parts = new List<BandPart>();
        int pos = 0, k = 0, gapOf = -1;
        for (int y = y0; y < y1; y++)
        {
            while (k < seq.Count && y >= pos + seq[k].N) { pos += seq[k].N; k++; }
            if (k >= seq.Count) break;
            var x = seq[k];
            var last = parts.Count > 0 ? parts[^1] : null;
            if (x.Extra) continue;
            if (x.Gap)
            {
                if (last is { Gap: true } && gapOf == k) last.Height++;
                else { parts.Add(new BandPart { Src = x.Y0, Height = 1, Gap = true, Span = x.Span, Cols = x.Cols ?? [], X0 = x.X0, X1 = x.X1 }); gapOf = k; }
                continue;
            }
            int s = x.Y0 + (y - pos);
            if (last is { Gap: false } && last.Src + last.Height == s) last.Height++;
            else parts.Add(new BandPart { Src = s, Height = 1 });
        }
        return parts;
    }

    /// <summary>Bas de l'écriture d'origine de la ligne (sans sa bande d'interligne).</summary>
    private static double InkBottom(PrintedLine l)
    {
        var ink = l.Cells.Where(c => !c.Ink.IsEmpty).ToList();
        return ink.Count > 0 ? ink.Max(c => c.Ink.Bottom) : l.Cells.Max(c => PageLayout.BaseBox(l, c).Bottom);
    }

    /// <summary>Bornes des rangées d'un tableau à filets, d'après ses filets.</summary>
    private static void RowBounds(Table t)
    {
        double l = t.ColumnXs[0], r = t.ColumnXs[^1];
        for (int i = 0; i < t.Rows.Count && i + 1 < t.RowYs.Count; i++) t.Rows[i].Bounds = RectD.FromEdges(l, t.RowYs[i], r, t.RowYs[i + 1]);
    }

    /// <summary>Ordre d'affichage : la rangée <paramref name="index"/> (nouvelle) se place juste après (ou avant) la rangée <paramref name="anchor"/> (indice d'avant).</summary>
    private static void OrderInsert(Table t, int index, int anchor, bool before = false)
    {
        if (t.RowOrder is null) return;
        var old = new Table { Rows = t.Rows.Take(t.Rows.Count - 1).ToList(), RowOrder = t.RowOrder };
        int Shift(int x) => x >= index ? x + 1 : x;
        var o = Lots.RowOrder(old).Select(Shift).ToList();
        int k = o.IndexOf(Shift(anchor));
        o.Insert(before ? k : k + 1, index);
        t.RowOrder = o;
    }

    private static void OrderRemove(Table t, int index)
    {
        if (t.RowOrder is null) return;
        var old = new Table { Rows = [.. t.Rows, new Row()], RowOrder = t.RowOrder };
        t.RowOrder = Lots.RowOrder(old).Where(x => x != index).Select(x => x > index ? x - 1 : x).ToList();
    }

    /// <summary>Zone verticale [haut, bas[ de la ligne dans sa rangée (de l'interligne au-dessus à celui d'en dessous).</summary>
    private static (int Top, int Bottom) LineBand(Location at, PrintedLine line)
    {
        var (t, r, i, row) = at;
        int top = i > 0 ? Arrangement.GapMid(row.Lines[i - 1], line) : Arrangement.CutY(t.RowYs[r]) + Arrangement.Filet;
        int bottom = i + 1 < row.Lines.Count ? Arrangement.GapMid(line, row.Lines[i + 1]) : Arrangement.CutY(t.RowYs[r + 1]);
        // Lignes qui se chevauchent (lecture parasite sur un fond tramé) : la bande contient toujours toute la ligne, dans sa rangée.
        var boxes = line.Cells.Select(f => PageLayout.BaseBox(line, f)).ToList();
        top = Math.Max(Arrangement.CutY(t.RowYs[r]) + Arrangement.Filet, Math.Min(top, (int)Math.Floor(boxes.Min(b => b.Y))));
        bottom = Math.Min(Arrangement.CutY(t.RowYs[r + 1]), Math.Max(bottom, (int)Math.Ceiling(boxes.Max(b => b.Bottom))));
        return (top, Math.Max(top + 1, bottom));
    }

    /// <summary>
    /// « Dupliquer la ligne » : rangée ajoutée juste sous la rangée de la ligne, avec une copie de la ligne.
    /// <paramref name="blank"/> : rangée vierge de même structure (filets du tableau, case de lot vide, désignation et prix vides).
    /// <paramref name="above"/> : la rangée est posée juste au-dessus de celle de la ligne (sinon juste en dessous).
    /// Renvoie la nouvelle ligne (la hauteur de son texte sur plusieurs lignes est à recalculer par l'écran).
    /// </summary>
    public static PrintedLine? Copy(Page page, PrintedLine line, bool blank = false, bool above = false, PrintedLine? from = null, bool force = false)
    {
        var src0 = from ?? line;
        var at = Locate(page, line);
        if (at is null || (!force && src0 == line && !CanCopy(page, line)) || (above && !CanAddAbove(page, line))) return null;
        var (t, r, _, _) = at;
        // Modèle de la rangée (from, même tableau) : hauteur, filets et place des cases ; line : où la poser.
        var sat = src0 == line ? at : Locate(page, src0);
        if (sat is null || sat.Table != t) return null;
        var (top, bottom) = LineBand(sat, src0);
        int filetTop = Arrangement.CutY(t.RowYs[sat.R]);
        var body = PartsOf(page, top, bottom);
        var cols = t.ColumnXs.Select(x => (int)Math.Floor(x + 0.5)).ToList();
        int x0 = (int)Math.Floor(t.ColumnXs[0]) - 4, x1 = (int)Math.Ceiling(t.ColumnXs[^1]) + 4;
        // Rangée la plus nette pour prolonger les filets : la plus longue rangée d'image, sinon l'interligne d'une rangée ajoutée.
        var longest = body.Where(p => !p.Gap).Aggregate((BandPart?)null, (a, p) => a is null || p.Height > a.Height ? p : a) ?? body.FirstOrDefault(p => p.Gap);
        int span = longest is { Gap: true } ? longest.Span : longest?.Height ?? 0;
        int bodyH = body.Sum(p => p.Height);
        var parts = PartsOf(page, filetTop, filetTop + Arrangement.Filet);
        if (blank && longest is not null)
            parts.Add(new BandPart { Src = longest.Src, Height = bodyH, Gap = true, Span = span, Cols = cols, X0 = x0, X1 = x1 });
        else parts.AddRange(body);
        // Sous le texte, autant d'espace qu'au-dessus (la ligne copiée peut venir d'une rangée de plusieurs lignes) :
        // un interligne propre, filets verticaux prolongés depuis la rangée la plus nette de la ligne copiée.
        double inkTop = Arrangement.LineTop(src0), inkBottom = InkBottom(src0);
        double spaceAbove = inkTop - top, below = bodyH - (inkBottom - top);
        int pad = Math.Min(40, Math.Max(0, (int)Math.Ceiling(spaceAbove - below)));
        if (pad > 0 && longest is not null && src0 == line) // rangée modèle : exactement sa hauteur
            parts.Add(new BandPart { Src = longest.Src, Height = pad, Gap = true, Span = span, Cols = cols, X0 = x0, X1 = x1 });
        int y = Arrangement.CutY(t.RowYs[above ? r : r + 1]);
        // Cases de la ligne, relevées avant que la bande ne décale la page.
        var src = src0.Cells.Select(f => (F: f, B: PageLayout.BaseBox(src0, f), Ink: f.Ink)).ToList();
        var band = PageLayout.InsertBand(page, y, parts, "Copy");
        // Ligne recopiée : mêmes cases, posées dans la nouvelle rangée (même place par rapport à leur rangée).
        int dy = y + Arrangement.Filet - top;
        var copy = new PrintedLine
        {
            Kind = blank ? LineKind.Article : src0.Kind, KindChosenByUser = !blank && src0.KindChosenByUser, Copy = true,
            AutoLot = page.Added != null, // ligne d'une page ajoutée : case de lot seulement avec « + »
            Cells = src.Select(c => new Field
            {
                Id = $"{page.Id}-{band.Id}-{c.F.Column}", Column = c.F.Column,
                OriginalText = blank ? "" : c.F.OriginalText, Text = blank ? "" : c.F.Text,
                Box = c.B with { Y = c.B.Y + dy }, Ink = c.Ink.IsEmpty || blank ? RectD.Empty : c.Ink with { Y = c.Ink.Y + dy },
                Align = c.F.Align, Indent = c.F.Indent, FontSize = c.F.FontSize, Manual = !blank && c.F.Manual,
                Strikes = blank ? [] : c.F.Strikes.Select(s => new Strike { Start = s.Start, Length = s.Length, Color = s.Color }).ToList(),
                Colors = blank ? [] : c.F.Colors.Select(s => new Strike { Start = s.Start, Length = s.Length, Color = s.Color }).ToList(),
            }).ToList(),
        };
        int index = above ? r : r + 1;
        t.RowYs.Insert(index, y + Arrangement.Cut);
        t.Rows.Insert(index, new Row { Lines = [copy], Inserted = band.Id });
        OrderInsert(t, index, r, above);
        RowBounds(t);
        PageEdits.ApplyLocks(page);
        return copy;
    }

    /// <summary>« Ajouter une ligne vide » au-dessus ou en dessous de la rangée de la ligne.</summary>
    public static PrintedLine? AddBlank(Page page, PrintedLine line, bool above = false) => Copy(page, line, blank: true, above: above);

    /// <summary>« Séparer ici » : la ligne et ce qui la suit dans sa rangée forment une rangée à part (filet tracé au-dessus).</summary>
    public static bool Split(Page page, PrintedLine line)
    {
        var at = Locate(page, line);
        if (at is null || !SplitOk(at.Row, at.I)) return false;
        var (t, r, i, row) = at;
        int y = Arrangement.GapMid(row.Lines[i - 1], line);
        int filetTop = Arrangement.CutY(t.RowYs[r]);
        var band = PageLayout.InsertBand(page, y, PartsOf(page, filetTop, filetTop + Arrangement.Filet), "Split");
        var moved = row.Lines.Skip(i).ToList();
        row.Lines.RemoveRange(i, moved.Count);
        t.RowYs.Insert(r + 1, y + Arrangement.Cut);
        t.Rows.Insert(r + 1, new Row { Lines = moved, Inserted = band.Id });
        OrderInsert(t, r + 1, r);
        RowBounds(t);
        PageEdits.ApplyLocks(page);
        return true;
    }

    /// <summary>
    /// Désignation tapée sur plusieurs lignes : la suite du texte (à partir de la ligne <paramref name="from"/>) part dans une
    /// rangée ajoutée juste en dessous (rangée vierge de même hauteur, valeurs vides). Renvoie la nouvelle ligne.
    /// </summary>
    public static PrintedLine? SplitTypedText(Page page, PrintedLine line, int from)
    {
        var f = line.Cells[0];
        var parts = f.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (from <= 0 || from >= parts.Length) return null;
        string head = string.Join("\n", parts.Take(from)).TrimEnd(), rest = string.Join("\n", parts.Skip(from)).Trim();
        if (rest.Length == 0 || head.Length == 0) return null;
        var copy = Copy(page, line, blank: true);
        if (copy is null) return null;
        copy.Cells[0].Text = rest;
        Strikes.Adjust(f, f.Text, head);
        f.Text = head;
        return copy;
    }

    /// <summary>Défait une rangée ajoutée : copie supprimée, ou rangée séparée remise dans celle du dessus.</summary>
    public static bool RemoveAdded(Page page, PrintedLine line)
    {
        if (AddedRowOf(page, line) is not var (at, band)) return false;
        var (t, r, _, row) = at;
        if (band.Kind == "Split")
        {
            if (r == 0) return false;
            t.Rows[r - 1].Lines.AddRange(row.Lines);
        }
        else foreach (var l in row.Lines) PageLayout.SetExtra(page, l, 0);
        t.Rows.RemoveAt(r);
        t.RowYs.RemoveAt(r);
        OrderRemove(t, r);
        PageLayout.RemoveBand(page, band);
        RowBounds(t);
        return true;
    }

    /// <summary>« Séparer en dessous (tracer un trait) » : une ligne suit dans la rangée.</summary>
    public static bool CanSplitBelow(Page page, PrintedLine line) =>
        !line.OutsideTable && Locate(page, line) is { } at && at.I + 1 < at.Row.Lines.Count && SplitOk(at.Row, at.I + 1);

    /// <summary>Trace un trait sous la ligne : ce qui la suit dans la rangée forme une rangée à part.</summary>
    public static bool SplitBelow(Page page, PrintedLine line) =>
        Locate(page, line) is { } at && at.I + 1 < at.Row.Lines.Count && Split(page, at.Row.Lines[at.I + 1]);

    /// <summary>
    /// « Ajouter une ligne TOTAL » sous la rangée de la ligne : rangée complète (comme une ligne vide), désignation « TOTAL »,
    /// montants calculés selon <paramref name="scope"/> (above | page | pages | all ; Auto : recalculés à chaque modification).
    /// </summary>
    public static PrintedLine? AddTotalLine(Page page, PrintedLine line, TotalScope? scope)
    {
        var t = Copy(page, line, blank: true);
        if (t is null) return null;
        t.Kind = LineKind.Total;
        t.KindChosenByUser = true;
        t.Cells[0].Text = "TOTAL";
        t.TotalScope = new TotalScope { Mode = scope?.Mode ?? "above", Pages = [.. scope?.Pages ?? []], Auto = scope?.Auto ?? true, Bold = scope?.Bold ?? false };
        return t;
    }

    private static readonly System.Text.RegularExpressions.Regex Amount =
        new(@"^\s*(?:m[ée]moire|[+-]?\d[\d\s.,]*)\s*€?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Ligne d'appui pour une ligne TOTAL ajoutée depuis le menu de la page : la dernière ligne d'objet du tableau (ou du dernier).</summary>
    public static PrintedLine? LastObjectLine(Page page, Table? table = null)
    {
        var tables = page.Tables.Where(t => GridTable(t) && (table is null || t == table)).ToList();
        for (int k = tables.Count - 1; k >= 0; k--)
        {
            var t = tables[k];
            var order = Lots.RowOrder(t).ToList();
            // De préférence la dernière ligne qui porte un montant (pas une lecture parasite d'un fond grisé).
            foreach (bool strict in new[] { true, false })
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    var row = t.Rows[order[i]];
                    for (int j = row.Lines.Count - 1; j >= 0; j--)
                    {
                        var l = row.Lines[j];
                        if (!CanCopy(page, l) || l.Deleted) continue;
                        if (!strict || l.Cells.Skip(1).Any(c => Amount.IsMatch(c.Text))) return l;
                    }
                }
        }
        return null;
    }

    // ---------- Réduire la rangée : enlever l'espace vide imprimé au-dessus ou en dessous du texte ----------

    /// <summary>Écriture de la ligne : encre lue (texte retapé : sa case), null si la ligne est vide.</summary>
    private static RectD? ContentOf(PrintedLine l)
    {
        RectD? r = null;
        foreach (var c in l.Cells)
        {
            if (string.IsNullOrWhiteSpace(c.Text)) continue;
            var b = !c.IsModified && !c.Ink.IsEmpty ? c.Ink : PageLayout.BaseBox(l, c);
            r = r is { } x ? x.Union(b) : b;
        }
        return r;
    }

    private sealed record Span(int Lines, int Top, int Bottom, int InkTop, int InkBottom);

    /// <summary>Haut et bas de l'écriture d'une rangée, et ses bornes utiles (sous le filet du haut, au-dessus de celui du bas).</summary>
    private static Span? RowSpan(Table t, int r)
    {
        var rects = t.Rows[r].Lines.Select(ContentOf).Where(x => x != null).Select(x => x!.Value).ToList();
        if (rects.Count == 0) return null;
        return new Span(rects.Count, Arrangement.CutY(t.RowYs[r]) + Arrangement.Filet, Arrangement.CutY(t.RowYs[r + 1]),
            (int)Math.Floor(rects.Min(x => x.Y)), (int)Math.Ceiling(rects.Max(x => x.Bottom)));
    }

    private static int Median(List<int> a) { var s = a.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

    /// <summary>Marges habituelles des rangées d'une ligne du tableau (au-dessus et en dessous du texte) et hauteur du texte.</summary>
    private static (int Top, int Bottom, int Height)? NormalPads(Table t)
    {
        List<int> top = [], bottom = [], height = [];
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            if (r + 1 >= t.RowYs.Count || row.Inserted != null || row.Lines.Any(l => l.Extra > 0)) continue;
            if (RowSpan(t, r) is not { Lines: 1 } s) continue;
            top.Add(s.InkTop - s.Top); bottom.Add(s.Bottom - s.InkBottom); height.Add(s.InkBottom - s.InkTop);
        }
        // Jamais moins de 4 px entre l'écriture et ce qu'on enlève (l'encre lue peut toucher le filet : marge négative).
        return top.Count > 0 ? (Math.Max(4, Median(top)), Math.Max(4, Median(bottom)), Median(height)) : null;
    }

    /// <summary>Rangées de pixels [y0, y0 + h[ toutes prises dans l'image d'origine, d'un seul tenant (pas dans une bande ajoutée).</summary>
    private static bool Contiguous(Page page, int y0, int h) =>
        PageLayout.ToBase(page, y0) is int a && PageLayout.ToBase(page, y0 + h - 1) is int b && b - a == h - 1;

    /// <summary>
    /// Espace vide en trop dans la rangée de la ligne (au-dessus et en dessous du texte), comparé aux autres rangées du tableau :
    /// (Y0, H) du bas vers le haut, ou null. Pas pour une ligne dupliquée ou vide ajoutée, une rangée agrandie ou qui contient
    /// une ligne supprimée (une rangée séparée, oui : seul son filet a été ajouté).
    /// </summary>
    public static List<(int Y0, int H)>? RowTrimOf(Page page, PrintedLine line)
    {
        // Page ajoutée : rangées vierges, rien d'imprimé à enlever (la hauteur se règle avec « Ligne plus / moins haute »).
        if (line.OutsideTable || page.Added != null || Locate(page, line) is not { } at) return null;
        var (t, r, _, row) = at;
        if (BandOf(page, row)?.Kind == "Copy" || row.Lines.Any(l => l.Deleted || l.Extra > 0)) return null;
        // Texte tapé sur plusieurs lignes : la place lui revient, on ne réduit pas.
        if (row.Lines.Any(l => l.Cells.Any(c => c.IsModified && (c.Text.Contains('\n') || c.Text.Contains('\r'))))) return null;
        if (NormalPads(t) is not { } pads || RowSpan(t, r) is not { } s) return null;
        int min = Math.Max(4, pads.Height / 2);
        var cuts = new List<(int, int)>();
        // Espace en trop au-dessus et en dessous du texte : enlevé s'il dépasse, en tout, la moitié d'une ligne.
        int below = Math.Max(0, s.Bottom - s.InkBottom - pads.Bottom), above = Math.Max(0, s.InkTop - s.Top - pads.Top);
        if (below + above < min) return null;
        if (below > 0 && Contiguous(page, s.Bottom - below, below)) cuts.Add((s.Bottom - below, below));
        if (above > 0 && Contiguous(page, s.Top, above)) cuts.Add((s.Top, above));
        return cuts.Count > 0 ? cuts : null;
    }

    /// <summary>« Réduire la rangée » : enlève l'espace vide en trop ; la rangée prend la hauteur des autres. Vrai si elle a changé.</summary>
    public static bool TrimRow(Page page, PrintedLine line)
    {
        if (RowTrimOf(page, line) is not { } cuts) return false;
        var row = Locate(page, line)!.Row;
        string? id = row.Lines.FirstOrDefault()?.Cells.FirstOrDefault()?.Id; // la réduction suit sa rangée
        foreach (var (y0, h) in cuts) PageLayout.CutBand(page, y0, h, id);
        RowBounds(Locate(page, line)!.Table);
        return true;
    }

    /// <summary>Bandes « Trim » de la rangée de la ligne (rangée réduite).</summary>
    public static List<InsertedBand> TrimsOf(Page page, PrintedLine line)
    {
        if (Locate(page, line) is not { } at) return [];
        var ids = at.Row.Lines.Select(l => l.Cells.FirstOrDefault()?.Id).ToHashSet();
        return page.Inserts.Where(b => b.Kind == "Trim" && b.Line != null && ids.Contains(b.Line)).ToList();
    }

    /// <summary>« Remettre la hauteur d'origine » : l'espace enlevé revient.</summary>
    public static bool UntrimRow(Page page, PrintedLine line)
    {
        var bands = TrimsOf(page, line).OrderByDescending(b => b.At).ToList();
        foreach (var b in bands) PageLayout.RemoveBand(page, b);
        if (bands.Count > 0) RowBounds(Locate(page, line)!.Table);
        return bands.Count > 0;
    }
}
