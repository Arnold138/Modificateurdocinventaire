using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Export;

/// <summary>
/// Lots 0 (absents, barrés) à l'export JPEG : la rangée entière est retirée de sa page (ce qui suit remonte)
/// et recollée en fin d'inventaire, sous le dernier tableau de la dernière page (ce qui suit descend).
/// Les filets sont gardés : la rangée part avec son filet du haut, le filet du bas reste en place.
/// </summary>
public static class ZeroRows
{
    /// <summary>Morceau de page découpé (une ou plusieurs rangées qui se suivent) et le bord gauche de son tableau.</summary>
    public sealed record Strip(Raster Image, int TableLeft);

    private static int R(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>Rangées à déplacer : tous les objets numérotés de la rangée sont au lot 0.</summary>
    public static List<(Table Table, int Row)> Rows(Page page)
    {
        var list = new List<(Table, int)>();
        foreach (var t in page.Tables.Where(t => t.HasGrid && t.RowYs.Count >= 2))
            for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
            {
                var objects = t.Rows[r].Lines.Where(l => l.Lot.Length > 0).ToList();
                if (objects.Count > 0 && objects.All(Lots.IsZero)) list.Add((t, r));
            }
        return list;
    }

    /// <summary>Bandes de pixels [Y0, Y1[ à retirer de la page (rangées voisines réunies).</summary>
    public static List<(int Y0, int Y1, int TableLeft)> Cuts(Raster image, Page page)
    {
        var cuts = new List<(int Y0, int Y1, int TableLeft)>();
        foreach (var (t, r) in Rows(page))
        {
            int th = LotColumn.MeasureRules(image, t).HThick;
            int y0 = R(t.RowYs[r]) - th / 2, y1 = R(t.RowYs[r + 1]) - th / 2;
            int left = R(t.ColumnXs.Count > 0 ? t.ColumnXs[0] : t.Bounds.Left);
            if (cuts.Count > 0 && cuts[^1].Y1 == y0 && cuts[^1].TableLeft == left) cuts[^1] = (cuts[^1].Y0, y1, left);
            else if (y1 > y0) cuts.Add((y0, y1, left));
        }
        cuts.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        return cuts;
    }

    /// <summary>Résultat du découpage d'une page.</summary>
    /// <param name="InsertAt">Où recoller des rangées sur cette page (sous le dernier tableau, avant son filet du bas), -1 s'il n'y a pas de tableau.</param>
    public sealed record CutResult(Raster Image, List<Strip> Strips, int InsertAt, int TableLeft);

    /// <summary>Retire les rangées au lot 0 : la page sans elles, les morceaux découpés (dans l'ordre) et le point d'insertion.</summary>
    public static CutResult Cut(Raster image, Page page)
    {
        var cuts = Cuts(image, page);
        var last = page.Tables.LastOrDefault(t => t.HasGrid && t.RowYs.Count >= 2);
        int insertAt = -1, tableLeft = 0;
        if (last != null)
        {
            int th = LotColumn.MeasureRules(image, last).HThick;
            insertAt = R(last.RowYs[^1]) - th / 2;
            insertAt -= cuts.Where(c => c.Y1 <= insertAt).Sum(c => c.Y1 - c.Y0);
            tableLeft = R(last.ColumnXs.Count > 0 ? last.ColumnXs[0] : last.Bounds.Left);
        }
        var strips = new List<Strip>();
        if (cuts.Count == 0) return new CutResult(image, strips, insertAt, tableLeft);
        int w = image.Width, ch = image.Channels, stride = w * ch;
        int height = image.Height - cuts.Sum(c => c.Y1 - c.Y0);
        var data = new byte[Math.Max(1, height) * stride];
        int src = 0, dst = 0;
        foreach (var (y0, y1, left) in cuts)
        {
            int a = Math.Clamp(y0, src, image.Height), b = Math.Clamp(y1, a, image.Height);
            if (a > src) { Array.Copy(image.Data, src * stride, data, dst * stride, (a - src) * stride); dst += a - src; }
            var strip = new byte[(b - a) * stride];
            Array.Copy(image.Data, a * stride, strip, 0, strip.Length);
            if (b > a) strips.Add(new Strip(new Raster(w, b - a, ch, strip), left));
            src = b;
        }
        if (image.Height > src) Array.Copy(image.Data, src * stride, data, dst * stride, (image.Height - src) * stride);
        return new CutResult(new Raster(w, Math.Max(1, height), ch, data), strips, insertAt, tableLeft);
    }

    /// <summary>Recolle les morceaux à la position <paramref name="at"/>, alignés sur le bord gauche du tableau d'arrivée.</summary>
    public static Raster Append(Raster image, int at, int tableLeft, IReadOnlyList<Strip> strips)
    {
        if (at < 0 || strips.Count == 0) return image;
        at = Math.Clamp(at, 0, image.Height);
        int w = image.Width, ch = image.Channels, stride = w * ch;
        int added = strips.Sum(s => s.Image.Height);
        var data = new byte[(image.Height + added) * stride];
        Array.Copy(image.Data, 0, data, 0, at * stride);
        var paper = Paper(image, Math.Max(0, at - 1));
        int dst = at;
        foreach (var s in strips)
        {
            int dx = tableLeft - s.TableLeft;
            var img = s.Image.Channels == ch ? s.Image : (ch == 4 ? s.Image.ToBgra() : s.Image.ToGray());
            for (int y = 0; y < img.Height; y++, dst++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx, o = dst * stride + x * ch;
                    if (sx >= 0 && sx < img.Width) Array.Copy(img.Data, (y * img.Width + sx) * ch, data, o, ch);
                    else Array.Copy(paper, 0, data, o, ch);
                }
        }
        Array.Copy(image.Data, at * stride, data, dst * stride, (image.Height - at) * stride);
        return new Raster(w, image.Height + added, ch, data);
    }

    private static byte[] Paper(Raster img, int y)
    {
        int ch = img.Channels;
        var best = new byte[ch];
        int bestL = -1;
        for (int x = 0; x < img.Width; x++)
        {
            int o = (y * img.Width + x) * ch;
            int l = ch == 1 ? img.Data[o] : img.Data[o] + img.Data[o + 1] + img.Data[o + 2];
            if (l > bestL) { bestL = l; Array.Copy(img.Data, o, best, 0, ch); }
        }
        return best;
    }
}
