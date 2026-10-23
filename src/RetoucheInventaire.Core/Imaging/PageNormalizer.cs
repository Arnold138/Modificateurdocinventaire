namespace RetoucheInventaire.Core.Imaging;

/// <summary>Résultat du redressement d'une page.</summary>
public sealed record NormalizedPage(Raster Image, Quad Crop, double SkewDegrees);

/// <summary>
/// Redresse une photo de document : perspective (4 coins) puis inclinaison résiduelle,
/// en un seul rééchantillonnage pour garder le texte net.
/// </summary>
public static class PageNormalizer
{
    public const int MaxSide = 2400;

    /// <param name="source">Image source déjà orientée (quarts de tour appliqués).</param>
    /// <param name="crop">Coins choisis à la main, ou null pour la détection automatique.</param>
    public static NormalizedPage Normalize(Raster source, Quad? crop = null)
    {
        var quad = crop ?? PageDetector.Detect(source);
        var (w, h) = OutputSize(quad);

        // Estimation de l'inclinaison sur une version réduite, puis rendu final unique.
        double preview = Math.Min(1.0, 1400.0 / Math.Max(w, h));
        int pw = Math.Max(1, (int)(w * preview)), ph = Math.Max(1, (int)(h * preview));
        var gray = source.ToGray();
        var flat = ImageOps.Warp(gray, pw, ph, ImageOps.Homography(quad, pw, ph));
        double skew = SkewEstimator.Estimate(flat);

        return new NormalizedPage(Render(source, quad, skew, w, h), quad, skew);
    }

    /// <summary>Rendu déterministe à partir des paramètres enregistrés dans le projet.</summary>
    public static Raster Render(Raster source, Quad crop, double skewDegrees, int width, int height)
    {
        bool identity = Math.Abs(skewDegrees) < 0.05 && width == source.Width && height == source.Height
                        && crop == Quad.FullImage(source.Width, source.Height);
        if (identity) return source;

        var hmap = ImageOps.Homography(crop, width, height);
        if (Math.Abs(skewDegrees) < 0.05) return ImageOps.Warp(source, width, height, hmap);

        double a = skewDegrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = width / 2.0, cy = height / 2.0;
        return ImageOps.Warp(source, width, height, (u, v) =>
        {
            double du = u - cx, dv = v - cy;
            return hmap(cx + cos * du - sin * dv, cy + sin * du + cos * dv);
        });
    }

    public static (int Width, int Height) OutputSize(Quad quad)
    {
        var (w, h) = quad.NaturalSize();
        double k = Math.Min(1.0, (double)MaxSide / Math.Max(w, h));
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }
}

/// <summary>
/// Mesure l'inclinaison des lignes (texte et filets) : l'angle qui rend
/// le profil horizontal des pixels sombres le plus « pointu ».
/// Convention : les lignes suivent y = c + x·tan(angle).
/// </summary>
public static class SkewEstimator
{
    public static double Estimate(Raster image, double maxDegrees = 5)
    {
        var gray = image.ToGray();
        int thr = Math.Min(ImageOps.Otsu(gray), 160);
        var xs = new List<int>(); var ys = new List<int>();
        for (int y = 0; y < gray.Height; y++)
            for (int x = 0; x < gray.Width; x++)
                if (gray.Data[y * gray.Width + x] < thr) { xs.Add(x); ys.Add(y); }
        if (xs.Count < 200) return 0;

        int step = Math.Max(1, xs.Count / 250_000);
        double Score(double deg)
        {
            double t = Math.Tan(deg * Math.PI / 180);
            int off = (int)Math.Ceiling(Math.Abs(t) * gray.Width) + 2;
            var hist = new int[gray.Height + 2 * off];
            for (int i = 0; i < xs.Count; i += step)
                hist[(int)Math.Round(ys[i] - xs[i] * t) + off]++;
            double s = 0;
            foreach (var v in hist) s += (double)v * v;
            return s;
        }

        double best = 0, bestScore = Score(0);
        for (double d = -maxDegrees; d <= maxDegrees + 1e-9; d += 0.1)
        {
            double sc = Score(d);
            if (sc > bestScore * 1.0001) { bestScore = sc; best = d; }
        }
        double center = best;
        for (double d = center - 0.1; d <= center + 0.1 + 1e-9; d += 0.02)
        {
            double sc = Score(d);
            if (sc > bestScore * 1.0001) { bestScore = sc; best = d; }
        }
        return Math.Round(best, 2);
    }
}
