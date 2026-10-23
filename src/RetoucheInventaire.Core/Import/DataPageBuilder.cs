using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Import;

/// <summary>
/// Construit une page « tableau » à partir de données déjà structurées (TXT, CSV, XLSX).
/// Pas d'image d'origine : la grille est dessinée par l'application, même édition, même export.
/// </summary>
public static class DataPageBuilder
{
    public const double PageWidth = 1240, Margin = 40, RowHeight = 36, FontSize = 18, CharWidth = 9.5;

    public static Page Build(string sourceName, IReadOnlyList<string[]> rows)
    {
        var page = new Page { Source = PageSource.Data, SourceName = sourceName };
        int nCols = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Length));

        // Largeurs proportionnelles au contenu, avec des minimums lisibles au doigt.
        var want = Enumerable.Range(0, nCols)
            .Select(c => Math.Clamp(rows.Select(r => c < r.Length ? r[c].Length : 0).DefaultIfEmpty(0).Max(), c == 0 ? 30 : 8, 60) * CharWidth + 16)
            .ToArray();
        double avail = PageWidth - 2 * Margin, total = want.Sum();
        double k = avail / total;
        var xs = new List<double> { Margin };
        foreach (var w in want) xs.Add(xs[^1] + w * k);

        var ys = new List<double> { Margin };
        for (int i = 0; i < Math.Max(1, rows.Count); i++) ys.Add(ys[^1] + RowHeight);

        var table = new Table
        {
            HasGrid = true,
            ColumnXs = xs,
            RowYs = ys,
            Bounds = RectD.FromEdges(xs[0], ys[0], xs[^1], ys[^1]),
        };

        for (int r = 0; r < rows.Count; r++)
        {
            var line = new PrintedLine();
            for (int c = 0; c < nCols; c++)
            {
                string text = c < rows[r].Length ? rows[r][c].Trim() : "";
                bool numeric = text.Length > 0 && text.All(ch => char.IsDigit(ch) || " ,.-+ ".Contains(ch));
                var box = RectD.FromEdges(xs[c] + 4, ys[r] + 4, xs[c + 1] - 4, ys[r + 1] - 4);
                line.Cells.Add(new Field
                {
                    Column = c,
                    OriginalText = text,
                    Text = text,
                    Ink = RectD.Empty,
                    Box = box,
                    Align = c > 0 && numeric ? TextAlign.Right : TextAlign.Left,
                    Indent = 4,
                    FontSize = FontSize,
                });
            }
            table.Rows.Add(new Row { Bounds = RectD.FromEdges(xs[0], ys[r], xs[^1], ys[r + 1]), Lines = [line] });
        }

        LineClassifier.Classify(table);
        page.Tables.Add(table);
        page.Width = (int)PageWidth;
        page.Height = (int)(ys[^1] + Margin);
        PageAnalyzer.AssignIds(page);
        return page;
    }
}
