using System.Text;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Export;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using ModelPage = RetoucheInventaire.Core.Model.Page;

namespace RetoucheInventaire.App.Services;

/// <summary>
/// Export JPEG : la photo d'origine, inchangée sauf dans les cases modifiées,
/// où le nouveau texte est écrit en noir sur la couleur du papier, comme à l'écran,
/// et colonne des numéros de lot tracée dans la marge gauche.
/// </summary>
public static class ImageExportService
{
    private const double LineHeightRatio = 1.15;

    /// <summary>Image d'une page avant les corrections : colonne des lots tracée, écriture d'origine mise en couleur.</summary>
    public static Raster PageRaster(ModelPage page, Raster stretched, bool lotFrame = true)
    {
        var device = CanvasDevice.GetSharedDevice();
        var closed = LotColumn.CloseTables(stretched, page);
        var image = lotFrame && LotColumn.FramedTables(page).Count > 0 ? LotColumn.Draw(closed, page) : closed;
        var rects = new List<(int, int, int, int, string)>();
        foreach (var line in page.AllLines())
        {
            bool bold = TextPlacement.IsBold(line);
            foreach (var f in line.Cells.Where(c => c.Colors.Count > 0))
                rects.AddRange(TextPlacement.ColorRects(line, f, BaseSize(device, f, PageLayout.BaseBox(line, f).Height, bold), (t, z) => Width(device, t, z, bold)));
        }
        return ImageExporter.Tint(image, rects);
    }

    /// <summary>Dessine les cases modifiées sur la page redressée (Win2D, même mise en page qu'à l'écran).</summary>
    public static Raster RenderEdits(ModelPage page, Raster flat, bool lotFrame = true)
    {
        var device = CanvasDevice.GetSharedDevice();
        var framed = lotFrame ? LotColumn.FramedTables(page) : new List<Table>();
        var bgra = PageRaster(page, flat, lotFrame).ToBgra();
        var original = flat.ToBgra();
        using var bitmap = CanvasBitmap.CreateFromBytes(device, bgra.Data, bgra.Width, bgra.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        using var source = CanvasBitmap.CreateFromBytes(device, original.Data, original.Width, original.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        using var target = new CanvasRenderTarget(device, bgra.Width, bgra.Height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.DrawImage(bitmap);
            var edited = ImageExporter.EditedFields(page).ToHashSet();
            var prof = InkProfile(device, page, flat);
            foreach (var line in page.AllLines())
                foreach (var f in line.Cells.Where(edited.Contains))
                {
                    // Fond : papier, ou trame grise de la rangée (totaux, en-têtes) répétée.
                    if (ImageExporter.CleanPatch(flat, page, line, f) is RectD patch)
                    {
                        for (double x = f.Box.X; x < f.Box.Right; x += patch.Width)
                        {
                            double w = Math.Min(patch.Width, f.Box.Right - x);
                            ds.DrawImage(source, new Rect(x, f.Box.Y, w, Math.Min(patch.Height, f.Box.Height)), new Rect(patch.X, patch.Y, w, Math.Min(patch.Height, f.Box.Height)));
                        }
                    }
                    else
                    {
                        var (r, g, b) = ImageExporter.PaperColor(flat, f.Box);
                        ds.FillRectangle(new Rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height), ColorHelper.FromArgb(255, r, g, b));
                    }
                    if (f.Text.Trim().Length == 0) continue;

                    // Même mise en page qu'à l'écran : désignation sur plusieurs lignes à la taille de l'imprimé,
                    // valeurs sur une ligne (réduites si besoin) ; gras sur les totaux et en-têtes.
                    bool bold = TextPlacement.IsBold(line);
                    var bb = PageLayout.BaseBox(line, f);
                    var (size, lines, top) = TextPlacement.Layout(line, f, f.Text, BaseSize(device, f, bb.Height, bold), (t, z) => Width(device, t, z, bold));
                    using var clip = ds.CreateLayer(1f, new Rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height));
                    int cursor = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string text = lines[i];
                        int at = f.Text.IndexOf(text, cursor, StringComparison.Ordinal);
                        int start = at < 0 ? cursor : at;
                        cursor = start + text.Length;
                        double x = TextPlacement.LineX(f, Width(device, text, size, bold));
                        double y = top + i * LineHeightRatio * size;
                        // Morceaux en couleur (rouge, bleu, vert), le reste en noir.
                        foreach (var (a, e, color) in Strikes.ColorRuns(f, start, start + text.Length))
                        {
                            string part = text[(a - start)..(e - start)];
                            var (cr, cg, cb) = Strikes.TextRgb(color);
                            if (cr == 0 && cg == 0 && cb == 0 && prof.N >= 3) cr = cg = cb = (byte)prof.Ink; // noir de l'encre du scan
                            InkText(ds, device, prof, part, x, y, size, bold, ColorHelper.FromArgb(255, cr, cg, cb));
                            x += Width(device, part, size, bold);
                        }
                    }
                }

            // Texte barré : un trait fin au milieu des lettres, rouge ou noir.
            foreach (var line in page.AllLines())
            {
                bool bold = TextPlacement.IsBold(line);
                foreach (var f in line.Cells)
                    foreach (var g in TextPlacement.StrikeSegments(line, f, BaseSize(device, f, PageLayout.BaseBox(line, f).Height, bold), (t, z) => Width(device, t, z, bold)))
                    {
                        var (sr, sg, sb) = TextPlacement.Rgb(g.Color);
                        ds.FillRectangle(new Rect(g.X0, g.Y - g.Thickness / 2, g.X1 - g.X0, g.Thickness), ColorHelper.FromArgb(255, sr, sg, sb));
                    }
            }

            // Numéros de lot : centrés dans la colonne, à la hauteur de l'objet, même taille que son texte, en noir.
            var labels = Lots.Labels(page);
            foreach (var table in page.Tables)
                foreach (var line in table.Rows.SelectMany(r => r.Lines).Where(l => l.Lot.Length > 0))
                {
                    var box = labels.First(l => l.Line == line).Box;
                    if (!framed.Contains(table))
                    {
                        var (pr, pg, pb) = ImageExporter.PaperColor(flat, box);
                        ds.FillRectangle(new Rect(box.X, box.Y, box.Width, box.Height), ColorHelper.FromArgb(255, pr, pg, pb));
                    }
                    var c0 = line.Cells[0];
                    double size = Math.Max(5, Math.Min(BaseSize(device, c0, c0.Box.Height - line.Extra, false), box.Height / LineHeightRatio));
                    CanvasTextLayout layout = Layout(device, line.Lot, size, false);
                    while (size > 5 && layout.LayoutBounds.Width > box.Width - 2)
                    {
                        layout.Dispose();
                        size -= 0.5;
                        layout = Layout(device, line.Lot, size, false);
                    }
                    using (layout)
                    {
                        double x = box.X + (box.Width - layout.LayoutBounds.Width) / 2;
                        double y = Math.Clamp(box.CenterY - 0.58 * size, box.Top, Math.Max(box.Top, box.Bottom - LineHeightRatio * size));
                        byte ink = (byte)(prof.N >= 3 ? prof.Ink : 0);
                        InkText(ds, device, prof, line.Lot, x, y, size, false, ColorHelper.FromArgb(255, ink, ink, ink));
                    }
                }
        }
        return new Raster(bgra.Width, bgra.Height, 4, target.GetPixelBytes());
    }

    // ---------- Écriture de l'imprimé (même règle que la version iPhone/iPad) ----------

    /// <summary>Écriture de l'imprimé sur une page : facteur de largeur, épaisseur des traits (fraction de la taille), gris de l'encre.</summary>
    public readonly record struct Profile(double Sx, double? Stem, int Ink, int N);

    private static int Gray(Raster img, int x, int y)
    {
        int o = (y * img.Width + x) * img.Channels;
        return img.Channels == 1 ? img.Data[o] : (img.Data[o] * 29 + img.Data[o + 1] * 150 + img.Data[o + 2] * 77) >> 8;
    }

    private static double? Median(List<double> v) { if (v.Count == 0) return null; v.Sort(); return v[v.Count / 2]; }

    /// <summary>Épaisseur des traits (largeur des passages sombres) sur des lignes horizontales d'une zone.</summary>
    private static void StemsIn(Raster img, int x0, int x1, IEnumerable<int> ys, List<double> output, int max)
    {
        foreach (int y in ys)
        {
            if (y < 0 || y >= img.Height) continue;
            int run = 0;
            for (int x = Math.Max(0, x0); x <= Math.Min(img.Width - 1, x1); x++)
            {
                if (Gray(img, x, y) < 140) run++;
                else { if (run > 0 && run <= max) output.Add(run); run = 0; }
            }
        }
    }

    /// <summary>Hauteur x (px) d'un morceau d'écriture : bande des lignes de pixels les plus denses.</summary>
    private static int? XHeightPx(Raster img, RectD ink)
    {
        int x0 = Math.Max(0, (int)Math.Floor(ink.X)), x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(ink.Right));
        int y0 = Math.Max(0, (int)Math.Floor(ink.Y)), y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(ink.Bottom));
        var rows = new List<int>();
        for (int y = y0; y <= y1; y++) { int n = 0; for (int x = x0; x <= x1; x++) if (Gray(img, x, y) < 140) n++; rows.Add(n); }
        if (rows.Count == 0) return null;
        int max = rows.Max();
        if (max < 4) return null;
        int top = rows.FindIndex(n => n >= 0.5 * max), bottom = rows.Count - 1;
        while (bottom > top && rows[bottom] < 0.5 * max) bottom--;
        int xh = bottom - top + 1;
        return xh >= 4 ? xh : null;
    }

    private static (int Xh, double Stem)? _timesRef;
    /// <summary>Écriture de l'app (Times New Roman) à 100 px, mesurée comme le scan : hauteur x et épaisseur des traits.</summary>
    private static (int Xh, double Stem) TimesRef(CanvasDevice device)
    {
        if (_timesRef is { } known) return known;
        const string sample = "1 armoire en métal avec 15 casiers";
        using var target = new CanvasRenderTarget(device, 1400, 170, 96);
        using var layout = Layout(device, sample, 100, false);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.DrawTextLayout(layout, 10, 130 - layout.LineMetrics[0].Baseline, Colors.Black);
        }
        var img = new Raster(1400, 170, 4, target.GetPixelBytes());
        double w = Math.Min(1380, layout.LayoutBounds.Width);
        int xh = XHeightPx(img, new RectD(10, 20, w, 140)) ?? 45;
        var runs = new List<double>();
        StemsIn(img, 10, (int)(10 + w), new[] { 130 - (int)Math.Round(0.1 * 100 * xh / 45), 130 - (int)Math.Round(0.18 * 100 * xh / 45), 130 - (int)Math.Round(0.26 * 100 * xh / 45) }, runs, 20);
        var r = (xh, Median(runs) ?? 9);
        _timesRef = r;
        return r;
    }

    /// <summary>
    /// Écriture de l'imprimé sur cette page, mesurée sur le scan : lettres plus ou moins hautes que Times New Roman à
    /// largeur égale (Sx), traits plus épais et couleur de l'encre. Le texte retapé est écrit de la même façon.
    /// </summary>
    public static Profile InkProfile(CanvasDevice device, ModelPage page, Raster img)
    {
        var refT = TimesRef(device);
        var ratios = new List<double>();
        var stems = new List<double>();
        var inks = new List<double>();
        foreach (var line in page.AllLines())
        {
            if (line.Kind is not (LineKind.Article or LineKind.Titre) || line.Cells.Count == 0) continue;
            var f = line.Cells[0];
            if (f.Text != f.OriginalText || f.OriginalText.Trim().Length < 6 || f.Ink.IsEmpty) continue;
            // Surtout des minuscules (la bande la plus dense est alors celle des minuscules : hauteur x).
            var letters = f.OriginalText.Where(char.IsLetter).ToList();
            if (letters.Count < 5 || letters.Count(c => c != char.ToUpperInvariant(c)) < 0.7 * letters.Count) continue;
            if (XHeightPx(img, f.Ink) is not int xhp) continue;
            double hs = xhp * 100.0 / refT.Xh;
            if (hs < 8) continue;
            ratios.Add(BaseSize(device, f, f.Box.Height + 1000, false) / hs);
            int y0 = (int)Math.Round(f.Ink.Bottom);
            int before = stems.Count;
            StemsIn(img, (int)Math.Floor(f.Ink.X), (int)Math.Ceiling(f.Ink.Right),
                new[] { y0 - (int)Math.Round(0.1 * hs), y0 - (int)Math.Round(0.18 * hs), y0 - (int)Math.Round(0.26 * hs) }, stems, (int)Math.Round(0.2 * hs));
            for (int k = before; k < stems.Count; k++) stems[k] /= hs;
            for (int x = (int)Math.Floor(f.Ink.X); x < f.Ink.Right; x += 2)
            {
                int yy = y0 - (int)Math.Round(0.18 * hs);
                if (x < 0 || x >= img.Width || yy < 0 || yy >= img.Height) continue;
                int g = Gray(img, x, yy);
                if (g < 90) inks.Add(g);
            }
        }
        double r = Math.Clamp(Median(ratios) ?? 1, 0.8, 1.2);
        double sx = Math.Abs(1 - r) < 0.05 ? 1 : r; // écart faible : les lettres gardent leur forme
        stems.Sort();
        double? stem = stems.Count > 0 ? stems[(int)(stems.Count * 0.4)] : null;
        int ink = (int)Math.Min(60, Median(inks) ?? 0);
        return new Profile(sx, stem, ink, ratios.Count);
    }

    /// <summary>
    /// Écrit <paramref name="text"/> (haut de ligne en <paramref name="y"/> à la taille <paramref name="size"/>) comme
    /// l'imprimé : même largeur, lettres rehaussées de 1/Sx, traits épaissis jusqu'à l'épaisseur mesurée sur le scan.
    /// </summary>
    private static void InkText(CanvasDrawingSession ds, CanvasDevice device, Profile p, string text, double x, double y, double size, bool bold, Windows.UI.Color color)
    {
        double sx = p.N >= 3 ? p.Sx : 1, h = size / sx;
        using var reference = Layout(device, "x", size, bold);
        double baseline = y + reference.LineMetrics[0].Baseline;
        using var layout = Layout(device, text, h, bold);
        double top = baseline - layout.LineMetrics[0].Baseline;
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)sx, 1, new Vector2((float)x, (float)baseline)) * saved;
        ds.DrawTextLayout(layout, (float)x, (float)top, color);
        if (p.N >= 3 && p.Stem is double stem)
        {
            // Le trait s'ajoute de part et d'autre : moitié de l'écart (et un peu moins : le scan est flou, il paraît plus gras).
            double want = stem * h, have = TimesRef(device).Stem * h / 100 * (bold ? 1.6 : 1);
            double extra = Math.Min(0.04 * h, 0.6 * (want - have));
            if (extra > 0.3)
            {
                using var geometry = CanvasGeometry.CreateText(layout);
                ds.DrawGeometry(geometry, (float)x, (float)top, color, (float)extra, new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round });
            }
        }
        ds.Transform = saved;
    }

    private static CanvasTextLayout Layout(CanvasDevice device, string text, double size, bool bold) =>
        new(device, text, new CanvasTextFormat
        {
            FontFamily = "Times New Roman", FontSize = (float)size, WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        }, float.MaxValue, float.MaxValue);

    /// <summary>Même règle qu'à l'écran : taille qui redonne la largeur du texte imprimé.</summary>
    private static double Width(CanvasDevice device, string text, double size, bool bold)
    {
        using var layout = Layout(device, text.Length == 0 ? " " : text, size, bold);
        return layout.LayoutBounds.Width;
    }

    private static double BaseSize(CanvasDevice device, Field f, double boxHeight, bool bold)
    {
        double size = f.FontSize;
        if (!f.Ink.IsEmpty && f.OriginalText.Length > 0)
        {
            using var reference = Layout(device, f.OriginalText, 100, bold);
            double w100 = reference.LayoutBounds.Width;
            if (w100 > 0) size = Math.Clamp(100 * f.Ink.Width / w100, 0.7 * f.FontSize, 1.3 * f.FontSize);
        }
        return Math.Max(5, Math.Min(size, boxHeight / LineHeightRatio));
    }

    /// <summary>Photo d'origine + cases modifiées → fichier JPEG.</summary>
    /// <summary>Image encodée en JPEG (même réglage que les fichiers exportés), pour le PDF.</summary>
    public static async Task<byte[]> EncodeJpegAsync(Raster image)
    {
        var bgra = image.ToBgra();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var options = new BitmapPropertySet();
        options.Add("ImageQuality", new BitmapTypedValue(0.93, PropertyType.Single));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bgra.Width, (uint)bgra.Height, 96, 96, bgra.Data);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public static async Task SaveJpegAsync(Raster image, string path)
    {
        var bgra = image.ToBgra();
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            var options = new BitmapPropertySet();
            options.Add("ImageQuality", new BitmapTypedValue(0.93, PropertyType.Single));
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, fs.AsRandomAccessStream(), options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bgra.Width, (uint)bgra.Height, 96, 96, bgra.Data);
            await encoder.FlushAsync();
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>« photo.jpg » → « photo - modifié.jpg » ; « doc.pdf – page 2 » → « doc - page 2 - modifié.jpg ».</summary>
    public static string OutputName(ModelPage page)
    {
        string name = page.SourceName.Replace(".pdf – page", " - page", StringComparison.OrdinalIgnoreCase);
        foreach (var ext in new[] { ".jpeg", ".jpg", ".png", ".tiff", ".tif", ".bmp" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString().Trim() + " - modifié.jpg";
    }
}
