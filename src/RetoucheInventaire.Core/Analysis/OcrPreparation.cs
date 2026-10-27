using RetoucheInventaire.Core.Imaging;

namespace RetoucheInventaire.Core.Analysis;

/// <summary>
/// Prépare l'image envoyée à l'OCR (l'image affichée, elle, n'est pas touchée) :
/// filets du tableau effacés (ils collent aux lettres et font sauter des caractères),
/// seuil adaptatif (supprime les fonds tramés et atténue les ombres),
/// agrandissement (l'OCR lit mieux un texte d'au moins ~20 px de haut).
/// </summary>
public static class OcrPreparation
{
    public const int TargetWidth = 2200;

    public static (Raster Image, double Scale) Prepare(Raster page, IReadOnlyList<TableGrid> grids)
    {
        var gray = page.ToGray();
        var data = (byte[])gray.Data.Clone();
        int W = gray.Width, H = gray.Height;

        int thr = Math.Clamp(ImageOps.Otsu(gray) + 30, 140, 200);
        foreach (var g in grids)
        {
            int top = (int)g.Bounds.Top - 3, bottom = (int)g.Bounds.Bottom + 3;
            int left = (int)g.Bounds.Left - 3, right = (int)g.Bounds.Right + 3;
            foreach (var x in g.ColumnXs) EraseRule(data, W, H, thr, vertical: true, x, top, bottom);
            foreach (var y in g.RowYs) EraseRule(data, W, H, thr, vertical: false, y, left, right);
        }

        double scale = Math.Clamp((double)TargetWidth / W, 1.0, 3.0);
        var up = ImageOps.Resize(new Raster(W, H, 1, data), (int)Math.Round(W * scale), (int)Math.Round(H * scale));
        return (AdaptiveThreshold(up), scale);
    }

    /// <summary>
    /// Efface un filet en suivant ses petites ondulations. Là où une lettre touche le trait
    /// (tache sombre trop large), seul le cœur du trait est effacé pour ne pas abîmer la lettre.
    /// </summary>
    private static void EraseRule(byte[] d, int W, int H, int thr, bool vertical, double pos, int from, int to)
    {
        int across = vertical ? W : H, along = vertical ? H : W;
        int Idx(int a, int c) => vertical ? a * W + c : c * W + a;
        int home = (int)Math.Round(pos), center = home;
        for (int a = Math.Max(0, from); a <= Math.Min(along - 1, to); a++)
        {
            int seed = -1;
            for (int k = 0; k <= 3 && seed < 0; k++)
            {
                if (center - k >= 0 && d[Idx(a, center - k)] < thr) seed = center - k;
                else if (center + k < across && d[Idx(a, center + k)] < thr) seed = center + k;
            }
            if (seed < 0) continue;
            int lo = seed, hi = seed;
            while (lo > 0 && seed - lo < 8 && d[Idx(a, lo - 1)] < thr) lo--;
            while (hi < across - 1 && hi - seed < 8 && d[Idx(a, hi + 1)] < thr) hi++;
            if (hi - lo + 1 <= 5)
            {
                // Trait seul : on l'efface avec son liseré gris.
                if (lo > 0 && d[Idx(a, lo - 1)] < 235) lo--;
                if (hi < across - 1 && d[Idx(a, hi + 1)] < 235) hi++;
                for (int c = lo; c <= hi; c++) d[Idx(a, c)] = 255;
                center = Math.Clamp((lo + hi) / 2, home - 3, home + 3);
            }
            else
            {
                for (int c = Math.Max(0, center - 1); c <= Math.Min(across - 1, center + 1); c++) d[Idx(a, c)] = 255;
            }
        }
    }

    /// <summary>
    /// Seconde lecture : l'OCR d'une page entière rate parfois un nombre isolé dans une case.
    /// On repère les champs vides qui contiennent pourtant de l'encre, pour les relire un par un.
    /// </summary>
    public static List<Model.Field> EmptyFieldsWithInk(Model.Page page, Raster image)
    {
        var gray = image.ToGray();
        int thr = Math.Min(ImageOps.Otsu(gray), 150);
        var result = new List<Model.Field>();
        foreach (var f in page.Tables.Where(t => t.HasGrid).SelectMany(t => t.Rows).SelectMany(r => r.Lines).SelectMany(l => l.Cells))
        {
            if (f.Locked || f.Text.Length > 0) continue;
            var r = f.Box.Inflate(-2, -1);
            int dark = 0;
            for (int y = Math.Max(0, (int)r.Top); y < Math.Min(gray.Height, (int)r.Bottom); y++)
                for (int x = Math.Max(0, (int)r.Left); x < Math.Min(gray.Width, (int)r.Right); x++)
                    if (gray.Data[y * gray.Width + x] < thr) dark++;
            if (dark >= 20) result.Add(f);
        }
        return result;
    }

    /// <summary>Image d'une seule case, nettoyée, agrandie ×3, avec une marge blanche.</summary>
    public static (Raster Image, double Scale, PointD Origin) PrepareCell(Raster page, RectD box)
    {
        var gray = page.ToGray();
        var r = box.Inflate(-1, 0);
        int x0 = Math.Max(0, (int)r.Left), y0 = Math.Max(0, (int)r.Top);
        int x1 = Math.Min(gray.Width, (int)Math.Ceiling(r.Right)), y1 = Math.Min(gray.Height, (int)Math.Ceiling(r.Bottom));
        int w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        var crop = new byte[w * h];
        for (int y = 0; y < h; y++) Array.Copy(gray.Data, (y0 + y) * gray.Width + x0, crop, y * w, w);
        var cropR = new Raster(w, h, 1, crop);

        const double scale = 3; const int margin = 24;
        var up = ImageOps.Resize(cropR, w * 3, h * 3);
        int thr = ImageOps.Otsu(up);
        var padded = Raster.CreateWhite(up.Width + 2 * margin, up.Height + 2 * margin, 1);
        for (int y = 0; y < up.Height; y++)
            for (int x = 0; x < up.Width; x++)
                padded.Data[(y + margin) * padded.Width + x + margin] = up.Data[y * up.Width + x] < thr ? (byte)0 : (byte)255;
        // Origine du repère de l'image préparée, exprimée en pixels de page.
        return (padded, scale, new PointD(x0 - margin / scale, y0 - margin / scale));
    }

    /// <summary>Applique le résultat de la seconde lecture à un champ.</summary>
    public static void ApplyCellWords(Model.Field field, IReadOnlyList<OcrWord> wordsInCellImage, double scale, PointD origin)
    {
        var words = wordsInCellImage
            .Select(w => w with { Text = w.Text.Trim().Trim('|') })
            .Where(w => w.Text.Length > 0)
            .OrderBy(w => w.Box.X).ToList();
        if (words.Count == 0) return;
        var text = string.Join(" ", words.Select(w => w.Text));
        var ink = words.Select(w => new RectD(origin.X + w.Box.X / scale, origin.Y + w.Box.Y / scale, w.Box.Width / scale, w.Box.Height / scale))
                       .Aggregate(RectD.Empty, (a, b) => a.Union(b));
        field.OriginalText = field.Text = PageAnalyzer.FixOcrText(text);
        field.Ink = ink;
    }

    /// <summary>Remet les mots dans le repère de la page redressée.</summary>
    public static List<OcrWord> MapBack(IEnumerable<OcrWord> words, double scale) =>
        words.Select(w => w with { Box = new RectD(w.Box.X / scale, w.Box.Y / scale, w.Box.Width / scale, w.Box.Height / scale) }).ToList();

    /// <summary>Seuil de Bradley : un pixel est noir s'il est nettement plus sombre que son voisinage.</summary>
    public static Raster AdaptiveThreshold(Raster gray, double t = 0.15)
    {
        int W = gray.Width, H = gray.Height;
        int s = Math.Max(15, W / 16) / 2;
        var integral = new long[(W + 1) * (H + 1)];
        for (int y = 0; y < H; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < W; x++)
            {
                rowSum += gray.Data[y * W + x];
                integral[(y + 1) * (W + 1) + x + 1] = integral[y * (W + 1) + x + 1] + rowSum;
            }
        }
        var outData = new byte[W * H];
        Parallel.For(0, H, y =>
        {
            int y0 = Math.Max(0, y - s), y1 = Math.Min(H - 1, y + s);
            for (int x = 0; x < W; x++)
            {
                int x0 = Math.Max(0, x - s), x1 = Math.Min(W - 1, x + s);
                long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long sum = integral[(y1 + 1) * (W + 1) + x1 + 1] - integral[y0 * (W + 1) + x1 + 1]
                         - integral[(y1 + 1) * (W + 1) + x0] + integral[y0 * (W + 1) + x0];
                outData[y * W + x] = gray.Data[y * W + x] * count < sum * (1 - t) ? (byte)0 : (byte)255;
            }
        });
        return new Raster(W, H, 1, outData);
    }
}
