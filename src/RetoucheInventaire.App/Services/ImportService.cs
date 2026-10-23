using System.Text.RegularExpressions;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Import;
using RetoucheInventaire.Core.Model;
using Windows.Storage;
using ModelPage = RetoucheInventaire.Core.Model.Page;

namespace RetoucheInventaire.App.Services;

/// <summary>Une page prête à afficher : le modèle, l'image redressée et la source orientée (pour recadrer).</summary>
public sealed record ImportedPage(ModelPage Page, Raster? Image, Raster? OrientedSource);

/// <summary>
/// Chaîne d'import : fichier → pages. Pour les images :
/// redressement → grille → OCR de la page → seconde lecture des cases vides → analyse.
/// </summary>
public sealed partial class ImportService(OcrService ocr)
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".pdf", ".txt", ".csv", ".xlsx"];

    [GeneratedRegex(@"^\p{L}{3,}$")]
    private static partial Regex WordLike();

    public async Task<List<ImportedPage>> ImportAsync(StorageFile file, IDictionary<string, byte[]> sources, IProgress<string> progress)
    {
        string ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var result = new List<ImportedPage>();
        switch (ext)
        {
            case ".txt":
            case ".csv":
            {
                var bytes = await File.ReadAllBytesAsync(file.Path);
                var rows = DelimitedReader.Parse(DelimitedReader.DecodeText(bytes), ext);
                result.Add(new(DataPageBuilder.Build(file.Name, rows), null, null));
                break;
            }
            case ".xlsx":
            {
                await using var fs = File.OpenRead(file.Path);
                foreach (var (sheet, rows) in XlsxReader.Read(fs))
                    result.Add(new(DataPageBuilder.Build($"{file.Name} – {sheet}", rows), null, null));
                break;
            }
            case ".pdf":
            {
                progress.Report($"{file.Name} : lecture du PDF…");
                var rendered = await PdfService.RenderPagesAsync(file);
                for (int i = 0; i < rendered.Count; i++)
                {
                    progress.Report($"{file.Name} : page {i + 1}/{rendered.Count}, reconnaissance du texte…");
                    var entry = NewEntry(".png");
                    sources[entry] = rendered[i];
                    result.Add(await FromImageAsync(rendered[i], entry, $"{file.Name} – page {i + 1}"));
                }
                break;
            }
            default:
            {
                progress.Report($"{file.Name} : redressement et reconnaissance du texte…");
                var bytes = await File.ReadAllBytesAsync(file.Path);
                var entry = NewEntry(ext);
                sources[entry] = bytes;
                result.Add(await FromImageAsync(bytes, entry, file.Name));
                break;
            }
        }
        return result;
    }

    private static string NewEntry(string ext) => $"sources/{Guid.NewGuid():N}{ext}";

    private async Task<ImportedPage> FromImageAsync(byte[] bytes, string entry, string name)
    {
        var source = await WinImaging.DecodeAsync(bytes);
        var page = new ModelPage { Source = PageSource.Image, SourceName = name, SourceEntry = entry };
        var (image, words) = await ProcessAsync(page, source, crop: null);

        // Page tournée d'un quart de tour (photo prise en paysage sans EXIF) : on essaie les autres sens.
        int found = Readable(words);
        if (found < 12)
        {
            int bestTurns = 0, best = found;
            for (int k = 1; k <= 3; k++)
            {
                var rotated = await Task.Run(() => ImageOps.Rotate90(image, k));
                var (prep, scale) = await Task.Run(() => OcrPreparation.Prepare(rotated, []));
                int n = Readable(await ocr.RecognizeAsync(prep));
                if (n > best) { best = n; bestTurns = k; }
            }
            if (bestTurns != 0 && best >= 12 && best > 2 * found)
            {
                page.QuarterTurns = bestTurns;
                source = await Task.Run(() => ImageOps.Rotate90(source, bestTurns));
                (image, _) = await ProcessAsync(page, source, crop: null);
            }
        }
        return new ImportedPage(page, image, source);
    }

    private static int Readable(IEnumerable<OcrWord> words) =>
        words.Count(w => WordLike().IsMatch(w.Text) && w.Text.IndexOfAny("aeiouyàâéèêëîïôûùAEIOUY".ToCharArray()) >= 0);

    /// <summary>
    /// Traite une source déjà orientée. Utilisé à l'import et après un recadrage manuel.
    /// Le modèle de page est rempli ; l'image redressée est renvoyée pour l'affichage.
    /// </summary>
    public async Task<(Raster Image, List<OcrWord> Words)> ProcessAsync(ModelPage page, Raster orientedSource, Quad? crop)
    {
        var normalized = await Task.Run(() => PageNormalizer.Normalize(orientedSource, crop));
        page.Crop = normalized.Crop;
        page.Skew = normalized.SkewDegrees;
        var image = normalized.Image;

        var grids = await Task.Run(() => RuleDetector.Detect(image));
        var (prep, scale) = await Task.Run(() => OcrPreparation.Prepare(image, grids));
        var words = OcrPreparation.MapBack(await ocr.RecognizeAsync(prep), scale);
        await Task.Run(() => PageAnalyzer.Analyze(page, image, words, grids));

        // Seconde lecture des cases vides qui contiennent de l'encre (nombres isolés oubliés).
        foreach (var field in OcrPreparation.EmptyFieldsWithInk(page, image))
        {
            var (cell, cellScale, origin) = OcrPreparation.PrepareCell(image, field.Box);
            OcrPreparation.ApplyCellWords(field, await ocr.RecognizeAsync(cell), cellScale, origin);
        }
        PageAnalyzer.Reclassify(page);
        return (image, words);
    }

    /// <summary>Recalcule l'image affichée d'une page enregistrée (déterministe, sans OCR).</summary>
    public static async Task<(Raster Image, Raster Source)> RestoreImageAsync(ModelPage page, byte[] sourceBytes)
    {
        var decoded = await WinImaging.DecodeAsync(sourceBytes);
        return await Task.Run(() =>
        {
            var oriented = ImageOps.Rotate90(decoded, page.QuarterTurns);
            var crop = page.Crop ?? Quad.FullImage(oriented.Width, oriented.Height);
            return (PageNormalizer.Render(oriented, crop, page.Skew, page.Width, page.Height), oriented);
        });
    }
}
