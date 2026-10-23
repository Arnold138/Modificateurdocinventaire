namespace RetoucheInventaire.Core.Imaging;

/// <summary>Opérations d'image simples (rééchantillonnage, seuil, perspective, rotation).</summary>
public static class ImageOps
{
    /// <summary>
    /// Construit une image de sortie outW × outH : chaque pixel de sortie (u, v) va chercher
    /// sa couleur au point <paramref name="map"/>(u, v) de la source (interpolation bilinéaire, blanc hors image).
    /// </summary>
    public static Raster Warp(Raster src, int outW, int outH, Func<double, double, PointD> map)
    {
        int ch = src.Channels;
        var dst = new byte[outW * outH * ch];
        int sw = src.Width, sh = src.Height;
        var s = src.Data;
        Parallel.For(0, outH, v =>
        {
            int o = v * outW * ch;
            for (int u = 0; u < outW; u++, o += ch)
            {
                var p = map(u, v);
                double x = p.X, y = p.Y;
                if (x < -0.5 || y < -0.5 || x > sw - 0.5 || y > sh - 0.5)
                {
                    for (int c = 0; c < ch; c++) dst[o + c] = 255;
                    continue;
                }
                x = Math.Clamp(x, 0, sw - 1);
                y = Math.Clamp(y, 0, sh - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Math.Min(x0 + 1, sw - 1), y1 = Math.Min(y0 + 1, sh - 1);
                double fx = x - x0, fy = y - y0;
                int i00 = (y0 * sw + x0) * ch, i10 = (y0 * sw + x1) * ch, i01 = (y1 * sw + x0) * ch, i11 = (y1 * sw + x1) * ch;
                for (int c = 0; c < ch; c++)
                {
                    double top = s[i00 + c] + (s[i10 + c] - s[i00 + c]) * fx;
                    double bot = s[i01 + c] + (s[i11 + c] - s[i01 + c]) * fx;
                    dst[o + c] = (byte)Math.Clamp(Math.Round(top + (bot - top) * fy), 0, 255);
                }
            }
        });
        return new Raster(outW, outH, ch, dst);
    }

    /// <summary>Redimensionnement. Réduction par moyenne de zone (gris), agrandissement bilinéaire.</summary>
    public static Raster Resize(Raster src, int outW, int outH)
    {
        if (outW == src.Width && outH == src.Height) return src;
        if (src.Channels == 1 && outW < src.Width && outH < src.Height) return ShrinkArea(src, outW, outH);
        double sx = (double)src.Width / outW, sy = (double)src.Height / outH;
        return Warp(src, outW, outH, (u, v) => new PointD((u + 0.5) * sx - 0.5, (v + 0.5) * sy - 0.5));
    }

    private static Raster ShrinkArea(Raster src, int outW, int outH)
    {
        var dst = new byte[outW * outH];
        double sx = (double)src.Width / outW, sy = (double)src.Height / outH;
        for (int v = 0; v < outH; v++)
        {
            int y0 = (int)(v * sy), y1 = Math.Max(y0 + 1, (int)((v + 1) * sy));
            for (int u = 0; u < outW; u++)
            {
                int x0 = (int)(u * sx), x1 = Math.Max(x0 + 1, (int)((u + 1) * sx));
                long sum = 0; int n = 0;
                for (int y = y0; y < y1 && y < src.Height; y++)
                    for (int x = x0; x < x1 && x < src.Width; x++) { sum += src.Data[y * src.Width + x]; n++; }
                dst[v * outW + u] = (byte)(n == 0 ? 255 : sum / n);
            }
        }
        return new Raster(outW, outH, 1, dst);
    }

    /// <summary>Seuil d'Otsu sur une image en gris.</summary>
    public static int Otsu(Raster gray)
    {
        var hist = new long[256];
        foreach (var b in gray.Data) hist[b]++;
        long total = gray.Data.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];
        double sumB = 0, best = -1; long wB = 0; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = t; }
        }
        return thr;
    }

    /// <summary>Rotation par quarts de tour dans le sens horaire (0 à 3).</summary>
    public static Raster Rotate90(Raster src, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return src;
        int w = src.Width, h = src.Height, ch = src.Channels;
        int ow = quarterTurns == 2 ? w : h, oh = quarterTurns == 2 ? h : w;
        var dst = new byte[ow * oh * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (quarterTurns)
                {
                    case 1: nx = h - 1 - y; ny = x; break;
                    case 2: nx = w - 1 - x; ny = h - 1 - y; break;
                    default: nx = y; ny = w - 1 - x; break;
                }
                Array.Copy(src.Data, (y * w + x) * ch, dst, (ny * ow + nx) * ch, ch);
            }
        return new Raster(ow, oh, ch, dst);
    }

    /// <summary>
    /// Homographie qui envoie les coins du rectangle de sortie (0,0)-(outW-1,outH-1)
    /// sur les coins du quadrilatère source.
    /// </summary>
    public static Func<double, double, PointD> Homography(Quad quad, int outW, int outH) =>
        Homography([new PointD(0, 0), new PointD(outW - 1, 0), new PointD(outW - 1, outH - 1), new PointD(0, outH - 1)], quad.ToArray());

    /// <summary>Homographie qui envoie chacun des 4 points <paramref name="dstPts"/> sur <paramref name="srcPts"/>.</summary>
    public static Func<double, double, PointD> Homography(PointD[] dstPts, PointD[] srcPts)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double u = dstPts[i].X, v = dstPts[i].Y, x = srcPts[i].X, y = srcPts[i].Y;
            int r = 2 * i;
            a[r, 0] = u; a[r, 1] = v; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -v * x; a[r, 8] = x;
            a[r + 1, 3] = u; a[r + 1, 4] = v; a[r + 1, 5] = 1; a[r + 1, 6] = -u * y; a[r + 1, 7] = -v * y; a[r + 1, 8] = y;
        }
        var h = SolveLinear(a, 8);
        return (u, v) =>
        {
            double d = h[6] * u + h[7] * v + 1;
            return new PointD((h[0] * u + h[1] * v + h[2]) / d, (h[3] * u + h[4] * v + h[5]) / d);
        };
    }

    private static double[] SolveLinear(double[,] a, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) throw new InvalidOperationException("Recadrage impossible : coins alignés.");
            if (piv != col) for (int c = 0; c <= n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (int c = col; c <= n; c++) a[r, c] -= f * a[col, c];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = a[i, n] / a[i, i];
        return x;
    }
}
