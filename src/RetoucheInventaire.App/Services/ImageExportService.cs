using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
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

    /// <summary>Dessine les cases modifiées sur la page redressée (Win2D, même mise en page qu'à l'écran).</summary>
    public static Raster RenderEdits(ModelPage page, Raster flat, bool lotFrame = true)
    {
        var device = CanvasDevice.GetSharedDevice();
        var framed = lotFrame ? LotColumn.FramedTables(page) : new List<Table>();
        var bgra = (framed.Count > 0 ? LotColumn.Draw(flat, page) : flat).ToBgra();
        using var bitmap = CanvasBitmap.CreateFromBytes(device, bgra.Data, bgra.Width, bgra.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        using var target = new CanvasRenderTarget(device, bgra.Width, bgra.Height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.DrawImage(bitmap);
            var edited = ImageExporter.EditedFields(page).ToHashSet();
            foreach (var line in page.AllLines())
                foreach (var f in line.Cells.Where(edited.Contains))
                {
                    var (r, g, b) = ImageExporter.PaperColor(flat, f.Box);
                    ds.FillRectangle(new Rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height), ColorHelper.FromArgb(255, r, g, b));
                    if (f.Text.Trim().Length == 0) continue;

                    // Même mise en page qu'à l'écran : désignation sur plusieurs lignes à la taille de l'imprimé,
                    // valeurs sur une ligne (réduites si besoin).
                    var bb = PageLayout.BaseBox(line, f);
                    double size = BaseSize(device, f, bb.Height);
                    double avail = Math.Max(4, f.Box.Width - f.Indent - 3);
                    List<string> lines;
                    if (TextWrap.IsMultiline(f))
                        lines = TextWrap.Wrap(f.Text, avail, t => Width(device, t, size));
                    else
                    {
                        while (size > 5 && Width(device, f.Text, size) > avail) size -= 0.5;
                        lines = [f.Text];
                    }
                    double center = f.Ink.IsEmpty ? bb.CenterY : f.Ink.CenterY;
                    double top = Math.Clamp(center - 0.58 * size, bb.Top, Math.Max(bb.Top, bb.Bottom - LineHeightRatio * size));
                    using var clip = ds.CreateLayer(1f, new Rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height));
                    for (int i = 0; i < lines.Count; i++)
                    {
                        using var layout = Layout(device, lines[i], size);
                        double w = layout.LayoutBounds.Width;
                        double x = f.Align switch
                        {
                            TextAlign.Right => f.Box.Right - f.Indent - w,
                            TextAlign.Center => f.Box.X + (f.Box.Width - w) / 2,
                            _ => f.Box.X + f.Indent,
                        };
                        ds.DrawTextLayout(layout, (float)x, (float)(top + i * LineHeightRatio * size), Colors.Black);
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
                    double size = Math.Max(5, Math.Min(BaseSize(device, c0, c0.Box.Height - line.Extra), box.Height / LineHeightRatio));
                    CanvasTextLayout layout = Layout(device, line.Lot, size);
                    while (size > 5 && layout.LayoutBounds.Width > box.Width - 2)
                    {
                        layout.Dispose();
                        size -= 0.5;
                        layout = Layout(device, line.Lot, size);
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

    private static CanvasTextLayout Layout(CanvasDevice device, string text, double size) =>
        new(device, text, new CanvasTextFormat { FontFamily = "Times New Roman", FontSize = (float)size, WordWrapping = CanvasWordWrapping.NoWrap }, float.MaxValue, float.MaxValue);

    /// <summary>Même règle qu'à l'écran : taille qui redonne la largeur du texte imprimé.</summary>
    private static double Width(CanvasDevice device, string text, double size)
    {
        using var layout = Layout(device, text.Length == 0 ? " " : text, size);
        return layout.LayoutBounds.Width;
    }

    private static double BaseSize(CanvasDevice device, Field f) => BaseSize(device, f, f.Box.Height);

    private static double BaseSize(CanvasDevice device, Field f, double boxHeight)
    {
        double size = f.FontSize;
        if (!f.Ink.IsEmpty && f.OriginalText.Length > 0)
        {
            using var reference = Layout(device, f.OriginalText, 100);
            double w100 = reference.LayoutBounds.Width;
            if (w100 > 0) size = Math.Clamp(100 * f.Ink.Width / w100, 0.7 * f.FontSize, 1.3 * f.FontSize);
        }
        return Math.Max(5, Math.Min(size, boxHeight / LineHeightRatio));
    }

    /// <summary>Photo d'origine + cases modifiées → fichier JPEG.</summary>
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
