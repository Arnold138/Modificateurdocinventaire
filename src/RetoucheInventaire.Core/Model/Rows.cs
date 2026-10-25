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
    public static bool CanCopy(Page page, PrintedLine line) =>
        !line.OutsideTable && (line.Kind is LineKind.Article or LineKind.Titre) && line.FirstText.Length > 0 && Locate(page, line) != null;

    /// <summary>« Séparer ici » : la ligne n'est pas la première de sa rangée.</summary>
    public static bool CanSplit(Page page, PrintedLine line) =>
        !line.OutsideTable && Locate(page, line) is { I: > 0 };

    /// <summary>Rangée ajoutée par l'utilisateur qui contient cette ligne (null sinon).</summary>
    public static (Location At, InsertedBand Band)? AddedRowOf(Page page, PrintedLine line)
    {
        var at = Locate(page, line);
        var band = at is null ? null : BandOf(page, at.Row);
        return band is null ? null : (at!, band);
    }

    /// <summary>Rangées de pixels (page agrandie) [y0, y1[ → morceaux de l'image d'origine.</summary>
    private static List<BandPart> PartsOf(Page page, int y0, int y1)
    {
        var parts = new List<BandPart>();
        for (int y = y0; y < y1; y++)
        {
            if (PageLayout.ToBase(page, y) is not int s) continue;
            if (parts.Count > 0 && !parts[^1].Gap && parts[^1].Src + parts[^1].Height == s) parts[^1].Height++;
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

    /// <summary>Ordre d'affichage : la rangée <paramref name="index"/> (nouvelle) se place juste après <paramref name="after"/>.</summary>
    private static void OrderInsert(Table t, int index, int after)
    {
        if (t.RowOrder is null) return;
        var old = new Table { Rows = t.Rows.Take(t.Rows.Count - 1).ToList(), RowOrder = t.RowOrder };
        var o = Lots.RowOrder(old).Select(x => x >= index ? x + 1 : x).ToList();
        o.Insert(o.IndexOf(after) + 1, index);
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
        return (top, Math.Max(top + 1, bottom));
    }

    /// <summary>
    /// « Dupliquer la ligne » : rangée ajoutée juste sous la rangée de la ligne, avec une copie de la ligne.
    /// <paramref name="blank"/> : rangée vierge de même hauteur (filets du tableau seulement) et cases vides, sans texte d'origine.
    /// Renvoie la nouvelle ligne (la hauteur de son texte sur plusieurs lignes est à recalculer par l'écran).
    /// </summary>
    public static PrintedLine? Copy(Page page, PrintedLine line, bool blank = false)
    {
        var at = Locate(page, line);
        if (at is null || !CanCopy(page, line)) return null;
        var (t, r, _, _) = at;
        var (top, bottom) = LineBand(at, line);
        int filetTop = Arrangement.CutY(t.RowYs[r]);
        var body = PartsOf(page, top, bottom);
        var cols = t.ColumnXs.Select(x => (int)Math.Floor(x + 0.5)).ToList();
        int x0 = (int)Math.Floor(t.ColumnXs[0]) - 4, x1 = (int)Math.Ceiling(t.ColumnXs[^1]) + 4;
        var longest = body.Where(p => !p.Gap).Aggregate((BandPart?)null, (a, p) => a is null || p.Height > a.Height ? p : a);
        int bodyH = body.Sum(p => p.Height);
        var parts = PartsOf(page, filetTop, filetTop + Arrangement.Filet);
        if (blank && longest is not null)
            parts.Add(new BandPart { Src = longest.Src, Height = bodyH, Gap = true, Span = longest.Height, Cols = cols, X0 = x0, X1 = x1 });
        else parts.AddRange(body);
        // Sous le texte, autant d'espace qu'au-dessus (la ligne copiée peut venir d'une rangée de plusieurs lignes) :
        // un interligne propre, filets verticaux prolongés depuis la rangée la plus nette de la ligne copiée.
        double inkTop = Arrangement.LineTop(line), inkBottom = InkBottom(line);
        double above = inkTop - top, below = bodyH - (inkBottom - top);
        int pad = Math.Min(40, Math.Max(0, (int)Math.Ceiling(above - below)));
        if (pad > 0 && longest is not null)
            parts.Add(new BandPart { Src = longest.Src, Height = pad, Gap = true, Span = longest.Height, Cols = cols, X0 = x0, X1 = x1 });
        int y = Arrangement.CutY(t.RowYs[r + 1]);
        var band = PageLayout.InsertBand(page, y, parts, "Copy");
        // Ligne recopiée : mêmes cases, posées dans la nouvelle rangée (même place par rapport à leur rangée).
        int dy = y + Arrangement.Filet - top;
        var copy = new PrintedLine
        {
            Kind = line.Kind, KindChosenByUser = line.KindChosenByUser, Copy = true,
            Cells = line.Cells.Select(f =>
            {
                var b = PageLayout.BaseBox(line, f);
                return new Field
                {
                    Id = $"{page.Id}-{band.Id}-{f.Column}", Column = f.Column,
                    OriginalText = blank ? "" : f.OriginalText, Text = blank ? "" : f.Text,
                    Box = b with { Y = b.Y + dy }, Ink = f.Ink.IsEmpty || blank ? RectD.Empty : f.Ink with { Y = f.Ink.Y + dy },
                    Align = f.Align, Indent = f.Indent, FontSize = f.FontSize, Manual = !blank && f.Manual,
                    Strikes = blank ? [] : f.Strikes.Select(s => new Strike { Start = s.Start, Length = s.Length, Color = s.Color }).ToList(),
                    Colors = blank ? [] : f.Colors.Select(s => new Strike { Start = s.Start, Length = s.Length, Color = s.Color }).ToList(),
                };
            }).ToList(),
        };
        t.RowYs.Insert(r + 1, y + Arrangement.Cut);
        t.Rows.Insert(r + 1, new Row { Lines = [copy], Inserted = band.Id });
        OrderInsert(t, r + 1, r);
        RowBounds(t);
        PageEdits.ApplyLocks(page);
        return copy;
    }

    /// <summary>« Séparer ici » : la ligne et ce qui la suit dans sa rangée forment une rangée à part (filet tracé au-dessus).</summary>
    public static bool Split(Page page, PrintedLine line)
    {
        var at = Locate(page, line);
        if (at is null || at.I == 0) return false;
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
}
