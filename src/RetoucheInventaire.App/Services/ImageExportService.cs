using System.Text;
using Microsoft.Graphics.Canvas;
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
                            using var layout = Layout(device, part, size, bold);
                            ds.DrawTextLayout(layout, (float)x, (float)y, ColorHelper.FromArgb(255, cr, cg, cb));
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
                        ds.DrawTextLayout(layout, (float)x, (float)y, Colors.Black);
                    }
                }
        }
        return new Raster(bgra.Width, bgra.Height, 4, target.GetPixelBytes());
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
