using System.Runtime.CompilerServices;
using RetoucheInventaire.Core.Imaging;

[assembly: InternalsVisibleTo("RetoucheInventaire.Core.Tests")]

namespace RetoucheInventaire.Core.Analysis;

/// <summary>Grille d'un tableau imprimé : bords des colonnes et des lignes, en pixels de page.</summary>
public sealed record TableGrid(RectD Bounds, IReadOnlyList<double> ColumnXs, IReadOnlyList<double> RowYs);

/// <summary>
/// Repère les filets (traits) des tableaux sur une page redressée.
/// Tolère les traits fins, un peu ondulés ou interrompus des scans.
/// </summary>
public static class RuleDetector
{
    public static List<TableGrid> Detect(Raster image)
    {
        var gray = image.ToGray();
        int W = gray.Width, H = gray.Height;
        int thr = Math.Clamp(ImageOps.Otsu(gray) + 30, 140, 200);
        var dark = new bool[W * H];
        for (int i = 0; i < dark.Length; i++) dark[i] = gray.Data[i] < thr;

        int gap = Math.Max(6, W / 150);
        var hRuns = HorizontalRuns(dark, W, H, gap, Math.Max(40, W / 10));
        var vLines = VerticalLines(dark, W, H, gap, Math.Max(30, H / 40));

        var tables = new List<TableGrid>();
        foreach (var g0 in GroupByVerticalOverlap(vLines))
        {
            // Un séparateur de colonne court au milieu d'un grand tableau est presque toujours du texte.
            double maxLen = g0.Max(l => l.Length);
            var group = g0.Where(l => l.Length >= 0.5 * maxLen).ToList();
            if (group.Count < 2) continue;
            var xs = Cluster(group.Select(l => l.X), 6);
            if (xs.Count < 2) continue;
            double top = group.Min(l => l.Y0), bottom = group.Max(l => l.Y1);
            double left = xs[0], right = xs[^1], width = right - left;
            if (width < W * 0.15) continue;

            // Filets horizontaux : rangées couvertes à 80 % sur toute la largeur du tableau.
            var candidates = new List<int>();
            for (int y = Math.Max(0, (int)top - 8); y <= Math.Min(H - 1, (int)bottom + 8); y++)
            {
                double covered = 0;
                foreach (var (s, e) in hRuns[y])
                    covered += Math.Max(0, Math.Min(e, right) - Math.Max(s, left));
                if (covered >= 0.8 * width) candidates.Add(y);
            }
            var ys = new List<double>();
            for (int i = 0; i < candidates.Count;)
            {
                int j = i;
                while (j + 1 < candidates.Count && candidates[j + 1] - candidates[j] <= 2) j++;
                ys.Add((candidates[i] + candidates[j]) / 2.0);
                i = j + 1;
            }
            if (ys.Count == 0 || ys[0] > top + 8) ys.Insert(0, top);
            if (ys[^1] < bottom - 8) ys.Add(bottom);
            ys = Cluster(ys, 8);
            if (ys.Count < 2) continue;

            tables.Add(new TableGrid(RectD.FromEdges(left, ys[0], right, ys[^1]), xs, ys));
        }
        return tables.OrderBy(t => t.Bounds.Top).ToList();
    }

    internal sealed record VLine(double X, double Y0, double Y1)
    {
        public double Length => Y1 - Y0;
    }

    private static List<(int S, int E)>[] HorizontalRuns(bool[] dark, int W, int H, int gap, int minLen)
    {
        var result = new List<(int, int)>[H];
        var band = new bool[W];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
                band[x] = dark[y * W + x] || (y > 0 && dark[(y - 1) * W + x]) || (y < H - 1 && dark[(y + 1) * W + x]);
            result[y] = Runs(band, W, gap, minLen);
        }
        return result;
    }

    internal static List<VLine> VerticalLines(bool[] dark, int W, int H, int gap, int minLen)
    {
        var segs = new List<(int X, int S, int E)>();
        var band = new bool[H];
        for (int x = 0; x < W; x++)
        {
            for (int y = 0; y < H; y++)
                band[y] = dark[y * W + x] || (x > 0 && dark[y * W + x - 1]) || (x < W - 1 && dark[y * W + x + 1]);
            foreach (var (s, e) in Runs(band, H, gap, minLen)) segs.Add((x, s, e));
        }

        // Fusion des segments proches en x et contigus en y.
        var lines = new List<(double SumX, int N, int Y0, int Y1, List<(int S, int E)> Parts)>();
        foreach (var seg in segs.OrderBy(s => s.X).ThenBy(s => s.S))
        {
            int found = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (Math.Abs(l.SumX / l.N - seg.X) <= 3 && seg.S <= l.Y1 + 15 && seg.E >= l.Y0 - 15) { found = i; break; }
            }
            if (found < 0) lines.Add((seg.X, 1, seg.S, seg.E, [(seg.S, seg.E)]));
            else
            {
                var l = lines[found];
                l.Parts.Add((seg.S, seg.E));
                lines[found] = (l.SumX + seg.X, l.N + 1, Math.Min(l.Y0, seg.S), Math.Max(l.Y1, seg.E), l.Parts);
            }
        }

        // Un vrai filet est presque continu ; des lettres empilées ne le sont pas.
        double minLine = Math.Max(50, H * 0.03);
        return lines
            .Where(l => Covered(l.Parts) >= 0.6 * (l.Y1 - l.Y0))
            .Select(l => new VLine(l.SumX / l.N, l.Y0, l.Y1))
            .Where(l => l.Length >= minLine && l.X > W * 0.01 && l.X < W * 0.99)
            .ToList();
    }

    private static int Covered(List<(int S, int E)> parts)
    {
        int total = 0, curS = -1, curE = -2;
        foreach (var (s, e) in parts.OrderBy(p => p.S))
        {
            if (s > curE + 1) { if (curE >= curS && curS >= 0) total += curE - curS + 1; curS = s; curE = e; }
            else curE = Math.Max(curE, e);
        }
        if (curS >= 0) total += curE - curS + 1;
        return total;
    }

    private static List<(int, int)> Runs(bool[] line, int n, int gap, int minLen)
    {
        var runs = new List<(int, int)>();
        int start = -1, last = -1;
        for (int i = 0; i < n; i++)
        {
            if (!line[i]) continue;
            if (start < 0) { start = last = i; }
            else if (i - last <= gap + 1) last = i;
            else
            {
                if (last - start + 1 >= minLen) runs.Add((start, last));
                start = last = i;
            }
        }
        if (start >= 0 && last - start + 1 >= minLen) runs.Add((start, last));
        return runs;
    }

    private static List<List<VLine>> GroupByVerticalOverlap(List<VLine> lines)
    {
        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                double overlap = Math.Min(lines[i].Y1, lines[j].Y1) - Math.Max(lines[i].Y0, lines[j].Y0);
                if (overlap >= 0.5 * Math.Min(lines[i].Length, lines[j].Length)) parent[Find(i)] = Find(j);
            }
        return lines.Select((l, i) => (l, root: Find(i)))
                    .GroupBy(t => t.root)
                    .Select(g => g.Select(t => t.l).ToList())
                    .ToList();
    }

    private static List<double> Cluster(IEnumerable<double> values, double tol)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var result = new List<double>();
        var bucket = new List<double>();
        foreach (var v in sorted)
        {
            if (bucket.Count > 0 && v - bucket[^1] > tol) { result.Add(bucket.Average()); bucket.Clear(); }
            bucket.Add(v);
        }
        if (bucket.Count > 0) result.Add(bucket.Average());
        return result;
    }
}
