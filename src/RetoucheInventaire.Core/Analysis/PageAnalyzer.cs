using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Analysis;

/// <summary>Un mot reconnu par l'OCR, avec sa position en pixels de page redressée.</summary>
public sealed record OcrWord(string Text, RectD Box);

/// <summary>
/// Transforme « image redressée + mots OCR » en tableaux, rangées, lignes imprimées et champs modifiables.
/// </summary>
public static class PageAnalyzer
{
    private const double Pad = 4; // marge entre un filet et la zone d'édition

    /// <param name="grids">Grilles déjà détectées (pour ne pas refaire le travail), ou null.</param>
    public static void Analyze(Page page, Raster image, IReadOnlyList<OcrWord> rawWords, IReadOnlyList<TableGrid>? grids = null)
    {
        page.Width = image.Width;
        page.Height = image.Height;
        page.Tables.Clear();
        page.LooseLines.Clear();

        var words = rawWords.Select(Clean).OfType<OcrWord>().ToList();
        var used = new bool[words.Count];

        foreach (var grid in grids ?? RuleDetector.Detect(image))
            page.Tables.Add(BuildGridTable(grid, words, used));

        var rest = words.Where((w, i) => !used[i]).ToList();
        if (page.Tables.Count == 0)
        {
            if (rest.Count > 0) page.Tables.Add(BuildGridlessTable(rest, page.Width));
        }
        else
        {
            foreach (var line in BuildGridlessTable(rest, page.Width).Rows.SelectMany(r => r.Lines))
            {
                line.OutsideTable = true;
                line.Kind = LineKind.Ignoree;
                page.LooseLines.Add(line);
            }
        }

        foreach (var t in page.Tables) LineClassifier.Classify(t);
        AssignIds(page);
        PageEdits.ApplyLocks(page);
    }

    /// <summary>Recalcule les types de ligne (après une seconde lecture), sans toucher aux choix de l'utilisateur.</summary>
    public static void Reclassify(Page page)
    {
        foreach (var t in page.Tables) LineClassifier.Classify(t);
    }

    /// <summary>
    /// Projet enregistré avec une version précédente : rangées sans texte lu (total sur fond gris) → cases vides modifiables ;
    /// rangée de libellés sans désignation (« exploitation réalisation » d'un récapitulatif) → en-tête. Rien d'autre ne change.
    /// Même règle que upgradePage (version iPhone/iPad).
    /// </summary>
    public static void Upgrade(Page page)
    {
        int n = 0;
        foreach (var t in page.Tables)
        {
            if (!t.HasGrid || t.ColumnXs.Count < 2 || t.RowYs.Count < 2) continue;
            var sizes = t.Rows.SelectMany(r => r.Lines).Select(l => l.Cells.FirstOrDefault()?.FontSize ?? 0).Where(v => v > 0).OrderBy(v => v).ToList();
            double em = sizes.Count > 0 ? sizes[sizes.Count / 2] : 12;
            bool header = MergeThinRowsOf(t);
            for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
            {
                var row = t.Rows[r];
                if (row.Lines.Count > 0) continue;
                double rowTop = t.RowYs[r], rowBottom = t.RowYs[r + 1];
                double top = rowTop + Pad, bottom = Math.Max(rowTop + Pad + 1, rowBottom - Pad);
                var line = new PrintedLine { Kind = r == 0 && header ? LineKind.EnTete : LineKind.Article };
                for (int c = 0; c + 1 < t.ColumnXs.Count; c++)
                {
                    var box = CellBox(t.ColumnXs[c], t.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, RectD.Empty);
                    line.Cells.Add(new Field
                    {
                        Id = $"{page.Id}-v{n++}", Column = c, Box = box, Align = c == 0 ? TextAlign.Left : TextAlign.Right, Indent = 4,
                        FontSize = Math.Min(em, Math.Max(6, box.Height / 1.05)),
                    });
                }
                row.Lines.Add(line);
            }
            for (int r = 1; r < t.Rows.Count; r++)
            {
                var lines = t.Rows[r].Lines;
                if (!LineClassifier.IsHeaderRow(lines)) continue;
                if (!lines.All(l => string.IsNullOrWhiteSpace(l.Cells.FirstOrDefault()?.Text) || LineClassifier.IsRomanHeading(l.Cells[0].Text))) continue;
                foreach (var l in lines.Where(l => !l.KindChosenByUser && !l.OutsideTable)) l.Kind = LineKind.EnTete;
            }
        }
    }

    /// <summary>
    /// Rangées fines d'une trame lue comme plusieurs filets (projet d'une version précédente) : réunies en une rangée (même règle
    /// que la détection). En-tête du tableau relu en morceaux (texte illisible) : remplacé par des cases vides modifiables,
    /// sauf s'il a été corrigé. Vrai si l'en-tête a été vidé. Même règle que mergeThinRowsOf (iPhone/iPad).
    /// </summary>
    private static bool MergeThinRowsOf(Table t)
    {
        if (t.RowOrder != null || t.Rows.Any(r => r.Inserted != null) || t.Rows.Count + 1 != t.RowYs.Count) return false;
        var keep = RuleDetector.MergeThinRows(t.RowYs);
        if (keep.Count == t.RowYs.Count) return false;
        var rows = new List<(Row Row, int N)>();
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            if (keep.Contains(t.RowYs[r])) { rows.Add((new Row { Bounds = row.Bounds, Lines = [.. row.Lines] }, 1)); continue; }
            var g = rows[^1];
            g.Row.Lines.AddRange(row.Lines);
            g.Row.Bounds = RectD.FromEdges(g.Row.Bounds.X, g.Row.Bounds.Y, Math.Max(g.Row.Bounds.Right, row.Bounds.Right), row.Bounds.Bottom);
            rows[^1] = (g.Row, g.N + 1);
        }
        bool header = false;
        if (rows.Count > 0 && rows[0].N > 1 && !rows[0].Row.Lines.Any(l => l.KindChosenByUser || l.Cells.Any(c => c.IsModified)))
        {
            rows[0].Row.Lines.Clear();
            header = true;
        }
        t.Rows = rows.Select(x => x.Row).ToList();
        t.RowYs = keep;
        return header;
    }

    public static void AssignIds(Page page)
    {
        int n = 0;
        foreach (var f in page.AllFields()) f.Id = $"{page.Id}-{n++}";
    }

    private static OcrWord? Clean(OcrWord w)
    {
        var t = w.Text.Trim().Trim('|', '¦');
        if (t.Length == 0 || t.All(c => "|¦_[]{}".Contains(c))) return null;
        if (w.Box.Width <= 0 || w.Box.Height <= 0) return null;
        return w with { Text = t };
    }

    // ---------- Tableaux avec filets ----------

    private static Table BuildGridTable(TableGrid grid, List<OcrWord> words, bool[] used)
    {
        var table = new Table { Bounds = grid.Bounds, HasGrid = true, ColumnXs = [.. grid.ColumnXs], RowYs = [.. grid.RowYs] };
        int nCols = grid.ColumnXs.Count - 1;
        var area = grid.Bounds.Inflate(2, 2);

        var rowWords = Enumerable.Range(0, grid.RowYs.Count - 1).Select(_ => new List<(OcrWord W, int Col)>()).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (used[i] || !area.Contains(w.Box.CenterX, w.Box.CenterY)) continue;
            int col = IndexOf(grid.ColumnXs, w.Box.CenterX);
            int row = IndexOf(grid.RowYs, w.Box.CenterY);
            rowWords[row].Add((w, col));
            used[i] = true;
        }

        for (int r = 0; r < rowWords.Count; r++)
        {
            double rowTop = grid.RowYs[r], rowBottom = grid.RowYs[r + 1];
            var row = new Row { Bounds = RectD.FromEdges(grid.ColumnXs[0], rowTop, grid.ColumnXs[^1], rowBottom) };
            var lines = GroupIntoLines(rowWords[r], t => t.W.Box);

            // Rangée où l'OCR n'a rien lu (fond gris tramé d'un total…) : des cases vides, modifiables et relues une par une.
            if (lines.Count == 0)
            {
                var blank = new PrintedLine();
                double top = rowTop + Pad, bottom = Math.Max(rowTop + Pad + 1, rowBottom - Pad);
                for (int c = 0; c < nCols; c++)
                    blank.Cells.Add(new Field { Column = c, Box = CellBox(grid.ColumnXs[c], grid.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, RectD.Empty), FontSize = 0 });
                row.Lines.Add(blank);
                table.Rows.Add(row);
                continue;
            }

            for (int li = 0; li < lines.Count; li++)
            {
                var lineWords = lines[li];
                var band = lineWords.Aggregate(RectD.Empty, (a, t) => a.Union(t.W.Box));
                double h = band.Height;
                double prevBottom = li > 0 ? lines[li - 1].Aggregate(RectD.Empty, (a, t) => a.Union(t.W.Box)).Bottom : double.NaN;
                double nextTop = li < lines.Count - 1 ? lines[li + 1].Aggregate(RectD.Empty, (a, t) => a.Union(t.W.Box)).Top : double.NaN;
                double top = Math.Max(rowTop + Pad, band.Top - 0.35 * h);
                double bottom = Math.Min(rowBottom - Pad, band.Bottom + 0.35 * h);
                if (!double.IsNaN(prevBottom)) top = Math.Max(top, (prevBottom + band.Top) / 2);
                if (!double.IsNaN(nextTop)) bottom = Math.Min(bottom, (band.Bottom + nextTop) / 2);
                double em = EstimateFontSize(lineWords.Select(t => t.W));

                var line = new PrintedLine();
                for (int c = 0; c < nCols; c++)
                {
                    var cellWords = lineWords.Where(t => t.Col == c).Select(t => t.W).OrderBy(w => w.Box.X).ToList();
                    string text = string.Join(" ", cellWords.Select(w => w.Text));
                    var ink = cellWords.Aggregate(RectD.Empty, (a, w) => a.Union(w.Box));
                    line.Cells.Add(new Field
                    {
                        Column = c,
                        OriginalText = text,
                        Text = text,
                        Ink = ink,
                        Box = CellBox(grid.ColumnXs[c], grid.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, ink),
                        FontSize = Math.Min(em, Math.Max(6, (bottom - top) / 1.05)),
                    });
                }
                row.Lines.Add(line);
            }
            table.Rows.Add(row);
        }

        ComputeAlignment(table, nCols);
        // Cases des rangées vides : taille d'écriture du tableau (médiane des autres lignes).
        var sizes = table.Rows.SelectMany(r => r.Lines).Select(l => l.Cells.FirstOrDefault()?.FontSize ?? 0).Where(v => v > 0).OrderBy(v => v).ToList();
        double tableEm = sizes.Count > 0 ? sizes[sizes.Count / 2] : 12;
        foreach (var f in table.Rows.SelectMany(r => r.Lines).SelectMany(l => l.Cells))
            if (f.FontSize == 0) f.FontSize = Math.Min(tableEm, Math.Max(6, f.Box.Height / 1.05));
        return table;
    }

    /// <summary>
    /// Zone d'édition : la case moins une marge pour ne pas mordre sur les filets,
    /// mais toujours assez grande pour recouvrir entièrement l'encre d'origine.
    /// </summary>
    private static RectD CellBox(double colLeft, double colRight, double rowTop, double rowBottom, double top, double bottom, RectD ink)
    {
        const double minGap = 1.5; // demi-épaisseur d'un filet
        double l = colLeft + Pad, r = colRight - Pad;
        if (!ink.IsEmpty)
        {
            l = Math.Max(colLeft + minGap, Math.Min(l, ink.Left - 2));
            r = Math.Min(colRight - minGap, Math.Max(r, ink.Right + 2));
            top = Math.Max(rowTop + minGap, Math.Min(top, ink.Top - 1));
            bottom = Math.Min(rowBottom - minGap, Math.Max(bottom, ink.Bottom + 1));
        }
        return RectD.FromEdges(l, top, r, bottom);
    }

    private static int IndexOf(IReadOnlyList<double> edges, double v)
    {
        for (int i = 0; i < edges.Count - 2; i++)
            if (v < edges[i + 1]) return i;
        return edges.Count - 2;
    }

    /// <summary>Alignement (gauche/centre/droite) et retrait de chaque champ, déduits de l'original.</summary>
    private static void ComputeAlignment(Table table, int nCols)
    {
        var fields = table.Rows.SelectMany(r => r.Lines).SelectMany(l => l.Cells).ToList();
        for (int c = 0; c < nCols; c++)
        {
            var col = fields.Where(f => f.Column == c).ToList();
            foreach (var f in col.Where(f => !f.Ink.IsEmpty))
            {
                double left = Math.Max(0, f.Ink.Left - f.Box.Left), right = Math.Max(0, f.Box.Right - f.Ink.Right);
                if (Math.Abs(left - right) < 0.15 * f.Box.Width) { f.Align = TextAlign.Center; f.Indent = 0; }
                else if (left < right) { f.Align = TextAlign.Left; f.Indent = left; }
                else { f.Align = TextAlign.Right; f.Indent = right; }
            }
            var filled = col.Where(f => !f.Ink.IsEmpty && f.Align != TextAlign.Center).ToList();
            var align = filled.Count == 0 ? (c == 0 ? TextAlign.Left : TextAlign.Right)
                : filled.GroupBy(f => f.Align).OrderByDescending(g => g.Count()).First().Key;
            var indents = filled.Where(f => f.Align == align).Select(f => f.Indent).OrderBy(v => v).ToList();
            double indent = indents.Count == 0 ? 2 : indents[indents.Count / 2];
            foreach (var f in col.Where(f => f.Ink.IsEmpty)) { f.Align = align; f.Indent = indent; }
        }
    }

    // ---------- Texte sans filets ----------

    /// <summary>
    /// Repli quand aucun filet n'est trouvé : une rangée par ligne de texte,
    /// colonnes devinées d'après les grands espaces entre les mots.
    /// </summary>
    private static Table BuildGridlessTable(List<OcrWord> words, int pageWidth)
    {
        var table = new Table { HasGrid = false };
        var lines = GroupIntoLines(words, w => w.Box);
        var bands = lines.Select(l => l.Aggregate(RectD.Empty, (a, w) => a.Union(w.Box))).ToList();

        for (int li = 0; li < lines.Count; li++)
        {
            var band = bands[li];
            double h = band.Height;
            double top = band.Top - 0.35 * h, bottom = band.Bottom + 0.35 * h;
            if (li > 0) top = Math.Max(top, (bands[li - 1].Bottom + band.Top) / 2);
            if (li < lines.Count - 1) bottom = Math.Min(bottom, (band.Bottom + bands[li + 1].Top) / 2);

            var sorted = lines[li].OrderBy(w => w.Box.X).ToList();
            double charW = sorted.Sum(w => w.Box.Width) / Math.Max(1, sorted.Sum(w => w.Text.Length));
            double splitGap = Math.Max(3 * charW, 1.2 * h);
            var segments = new List<List<OcrWord>> { new() { sorted[0] } };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Box.Left - sorted[i - 1].Box.Right > splitGap) segments.Add([]);
                segments[^1].Add(sorted[i]);
            }

            double em = EstimateFontSize(sorted);
            var line = new PrintedLine();
            for (int s = 0; s < segments.Count; s++)
            {
                var ink = segments[s].Aggregate(RectD.Empty, (a, w) => a.Union(w.Box));
                double right = s < segments.Count - 1
                    ? segments[s + 1][0].Box.Left - 6
                    : Math.Max(ink.Right, pageWidth * 0.98);
                string text = string.Join(" ", segments[s].Select(w => w.Text));
                line.Cells.Add(new Field
                {
                    Column = s,
                    OriginalText = text,
                    Text = text,
                    Ink = ink,
                    Box = RectD.FromEdges(ink.Left - 2, top, right, bottom),
                    Align = TextAlign.Left,
                    Indent = 2,
                    FontSize = Math.Min(em, Math.Max(6, bottom - top)),
                });
            }
            table.Rows.Add(new Row { Bounds = line.Bounds, Lines = [line] });
        }
        table.Bounds = table.Rows.Aggregate(RectD.Empty, (a, r) => a.Union(r.Bounds));
        return table;
    }

    // ---------- Outils ----------

    /// <summary>Regroupe des éléments en lignes de texte selon leur recouvrement vertical.</summary>
    private static List<List<T>> GroupIntoLines<T>(IEnumerable<T> items, Func<T, RectD> box)
    {
        var lines = new List<(RectD Band, List<T> Items)>();
        foreach (var it in items.OrderBy(i => box(i).CenterY))
        {
            var b = box(it);
            int best = -1; double bestOverlap = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                double ov = lines[i].Band.VerticalOverlap(b);
                if (ov > bestOverlap) { bestOverlap = ov; best = i; }
            }
            if (best >= 0 && bestOverlap >= 0.4 * Math.Min(lines[best].Band.Height, b.Height))
                lines[best] = (lines[best].Band.Union(b), lines[best].Items.Append(it).ToList());
            else
                lines.Add((b, [it]));
        }
        return lines.OrderBy(l => l.Band.Top).Select(l => l.Items).ToList();
    }

    /// <summary>
    /// Taille de police (em, en pixels) estimée d'après la hauteur des mots :
    /// un mot sans jambage ni majuscule est plus petit qu'un mot avec « p » ou « L ».
    /// </summary>
    public static double EstimateFontSize(IEnumerable<OcrWord> words)
    {
        var ems = words.Select(w => w.Box.Height / HeightRatio(w.Text)).OrderBy(v => v).ToList();
        return ems.Count == 0 ? 12 : ems[ems.Count / 2];
    }

    private static double HeightRatio(string t)
    {
        bool desc = t.IndexOfAny("gjpqyQJ,;()[]{}/@çµ".ToCharArray()) >= 0;
        bool asc = t.Any(ch => char.IsUpper(ch) || char.IsDigit(ch) || "bdfhklt'\"àâäéèêëîïôöûùüÿ!?%&#€$+".Contains(ch));
        if (desc && asc) return 0.93;
        if (desc) return 0.72;
        if (asc) return 0.70;
        return 0.47;
    }
}
