using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;
using Windows.Media.Ocr;
using OcrWord = RetoucheInventaire.Core.Analysis.OcrWord;

namespace RetoucheInventaire.App.Services;

/// <summary>
/// OCR hors ligne intégré à Windows (Windows.Media.Ocr) : aucune bibliothèque à embarquer,
/// rien n'est envoyé sur Internet. Nécessite la reconnaissance optique française installée dans Windows.
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine? _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("fr-FR"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool IsAvailable => _engine != null;

    public string LanguageTag => _engine?.RecognizerLanguage.LanguageTag ?? "";

    public bool IsFrench => LanguageTag.StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    public async Task<List<OcrWord>> RecognizeAsync(Raster image)
    {
        if (_engine is null)
            throw new InvalidOperationException("Aucune langue de reconnaissance de texte n'est installée dans Windows (voir le README).");

        var img = image;
        double k = 1;
        if (Math.Max(img.Width, img.Height) > OcrEngine.MaxImageDimension)
        {
            k = (double)OcrEngine.MaxImageDimension / Math.Max(img.Width, img.Height);
            img = ImageOps.Resize(img.ToGray(), (int)(img.Width * k), (int)(img.Height * k));
        }

        using var bitmap = WinImaging.ToSoftwareBitmap(img);
        var result = await _engine.RecognizeAsync(bitmap);
        return result.Lines
            .SelectMany(l => l.Words)
            .Select(w => new OcrWord(w.Text, new RectD(w.BoundingRect.X / k, w.BoundingRect.Y / k, w.BoundingRect.Width / k, w.BoundingRect.Height / k)))
            .ToList();
    }
}
