using System.Globalization;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Tests;

/// <summary>
/// Pages fournies par l'utilisateur. Les mots OCR (fichiers .words.tsv) ont été produits une fois
/// avec Tesseract sur la page redressée, pour simuler Windows.Media.Ocr hors de Windows.
/// </summary>
public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d);
            return d is null ? throw new DirectoryNotFoundException("tests/fixtures introuvable") : Path.Combine(d, "tests", "fixtures");
        }
    }

    private static readonly Dictionary<string, (Raster Image, List<OcrWord> Words)> Cache = [];

    public static (Raster Image, List<OcrWord> Words) Load(string name)
    {
        if (Cache.TryGetValue(name, out var c)) return c;
        var src = Pgm.Read(Path.Combine(Dir, name + ".pgm.gz"));
        var norm = PageNormalizer.Normalize(src).Image;
        var words = new List<OcrWord>();
        foreach (var line in File.ReadAllLines(Path.Combine(Dir, name + ".words.tsv")))
        {
            var p = line.Split('\t');
            if (p.Length < 5) continue;
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            words.Add(new OcrWord(p[0], new RectD(D(p[1]), D(p[2]), D(p[3]), D(p[4]))));
        }
        return Cache[name] = (norm, words);
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public static Page Analyze(string name)
    {
        var (img, words) = Load(name);
        var page = new Page { Source = PageSource.Image, SourceName = name + ".jpg", Id = name };
        PageAnalyzer.Analyze(page, img, words);

        // Seconde lecture des cases vides (résultats Tesseract enregistrés, comme le ferait l'application).
        var rereadFile = Path.Combine(Dir, name + ".reread.tsv");
        if (File.Exists(rereadFile))
        {
            var byId = File.ReadAllLines(rereadFile).Select(l => l.Split('\t')).Where(p => p.Length >= 6)
                .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Select(p =>
                    new OcrWord(p[1], new RectD(D(p[2]), D(p[3]), D(p[4]), D(p[5])))).ToList());
            foreach (var f in OcrPreparation.EmptyFieldsWithInk(page, img))
                if (byId.TryGetValue(f.Id, out var cellWords))
                {
                    var (_, scale, origin) = OcrPreparation.PrepareCell(img, f.Box);
                    OcrPreparation.ApplyCellWords(f, cellWords, scale, origin);
                }
            PageAnalyzer.Reclassify(page);
        }
        return page;
    }
}
