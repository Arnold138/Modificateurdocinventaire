namespace RetoucheInventaire.Core.Imaging;

/// <summary>
/// Trouve automatiquement les 4 coins de la feuille sur une photo (feuille claire sur fond plus sombre).
/// Méthode volontairement simple : seuil, plus grande zone claire, points extrêmes.
/// Si rien de fiable n'est trouvé (scan, PDF, capture), la page entière est gardée.
/// </summary>
public static class PageDetector
{
    private const int WorkSize = 500;

    public static Quad Detect(Raster image)
    {
        var gray = image.ToGray();
        int W = gray.Width, H = gray.Height;
        double scale = Math.Min(1.0, (double)WorkSize / Math.Max(W, H));
        int w = Math.Max(1, (int)Math.Round(W * scale)), h = Math.Max(1, (int)Math.Round(H * scale));
        var small = ImageOps.Resize(gray, w, h);
        double sx = (double)W / w, sy = (double)H / h;

        int thr = ImageOps.Otsu(small);
        var bright = new bool[w * h];
        int brightCount = 0;
        for (int i = 0; i < bright.Length; i++)
            if (small.Data[i] > thr) { bright[i] = true; brightCount++; }

        var full = Quad.FullImage(W, H);
        if (brightCount > 0.97 * w * h) return full;

        // Plus grande composante claire (4-connexité).
        var label = new int[w * h];
        var queue = new int[w * h];
        int bestLabel = 0, bestSize = 0, current = 0;
        for (int start = 0; start < label.Length; start++)
        {
            if (!bright[start] || label[start] != 0) continue;
            current++;
            int head = 0, tail = 0, size = 0;
            queue[tail++] = start; label[start] = current;
            while (head < tail)
            {
                int p = queue[head++]; size++;
                int x = p % w, y = p / w;
                if (x > 0 && bright[p - 1] && label[p - 1] == 0) { label[p - 1] = current; queue[tail++] = p - 1; }
                if (x < w - 1 && bright[p + 1] && label[p + 1] == 0) { label[p + 1] = current; queue[tail++] = p + 1; }
                if (y > 0 && bright[p - w] && label[p - w] == 0) { label[p - w] = current; queue[tail++] = p - w; }
                if (y < h - 1 && bright[p + w] && label[p + w] == 0) { label[p + w] = current; queue[tail++] = p + w; }
            }
            if (size > bestSize) { bestSize = size; bestLabel = current; }
        }
        if (bestSize < 0.2 * w * h) return full;

        // Coins = points extrêmes de x+y et x-y.
        double minS = double.MaxValue, maxS = double.MinValue, minD = double.MaxValue, maxD = double.MinValue;
        PointD tl = default, br = default, tr = default, bl = default;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (label[y * w + x] != bestLabel) continue;
                double s = x + y, d = x - y;
                if (s < minS) { minS = s; tl = new(x, y); }
                if (s > maxS) { maxS = s; br = new(x, y); }
                if (d > maxD) { maxD = d; tr = new(x, y); }
                if (d < minD) { minD = d; bl = new(x, y); }
            }

        // Retour à la pleine résolution, avec un léger retrait vers l'intérieur pour exclure le fond.
        var quad = new Quad(Up(tl), Up(tr), Up(br), Up(bl));
        quad = Inset(quad, Math.Max(sx, sy) * 1.5);
        if (quad.Area() < 0.2 * W * H) return full;

        double tol = 0.02 * Math.Max(W, H);
        bool nearFull = quad.ToArray().Zip(full.ToArray()).All(p => p.First.DistanceTo(p.Second) < tol);
        return nearFull ? full : quad;

        PointD Up(PointD p) => new(Math.Clamp((p.X + 0.5) * sx - 0.5, 0, W - 1), Math.Clamp((p.Y + 0.5) * sy - 0.5, 0, H - 1));
    }

    private static Quad Inset(Quad q, double d)
    {
        var pts = q.ToArray();
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        PointD Move(PointD p)
        {
            double dx = cx - p.X, dy = cy - p.Y, len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-6 ? p : new PointD(p.X + dx / len * d, p.Y + dy / len * d);
        }
        return new Quad(Move(q.TopLeft), Move(q.TopRight), Move(q.BottomRight), Move(q.BottomLeft));
    }
}
