using System.Text.Json;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Tests;

// Outils : préparation des pages pour l'OCR de test et inspection.
if (args.Length > 0 && args[0] == "normalize")
{
    var src = Pgm.Read(args[1]);
    var n = PageNormalizer.Normalize(src);
    Pgm.Write(args[2], n.Image);
    Console.WriteLine($"{Path.GetFileName(args[1])}: crop={n.Crop} skew={n.SkewDegrees} size={n.Image.Width}x{n.Image.Height}");
    return 0;
}
if (args.Length > 0 && args[0] == "prepare")
{
    // Image envoyée à l'OCR, telle que l'application la prépare pour Windows.Media.Ocr.
    var norm = PageNormalizer.Normalize(Pgm.Read(args[1])).Image;
    var (img, scale) = OcrPreparation.Prepare(norm, RuleDetector.Detect(norm));
    Pgm.Write(args[2], img);
    Console.WriteLine($"{Path.GetFileName(args[1])}: x{scale:0.###}");
    return 0;
}
if (args.Length > 0 && args[0] == "vlines")
{
    var (img, _) = Fixtures.Load(args[1]);
    var g = img.ToGray();
    int thr = Math.Clamp(ImageOps.Otsu(g) + 30, 140, 200);
    var dark = g.Data.Select(b => b < thr).ToArray();
    foreach (var l in RuleDetector.VerticalLines(dark, g.Width, g.Height, Math.Max(6, g.Width / 150), Math.Max(30, g.Height / 40)))
        Console.WriteLine($"x={l.X:0.0} y={l.Y0}-{l.Y1}");
    return 0;
}
if (args.Length > 0 && args[0] == "reread-list")
{
    // Cases vides contenant de l'encre : images à relire (fixtures de seconde lecture).
    var (img, words) = Fixtures.Load(args[1]);
    var page = new Page { Source = PageSource.Image, Id = args[1] };
    PageAnalyzer.Analyze(page, img, words);
    Directory.CreateDirectory(args[2]);
    foreach (var f in OcrPreparation.EmptyFieldsWithInk(page, img))
    {
        var (cell, _, _) = OcrPreparation.PrepareCell(img, f.Box);
        Pgm.Write(Path.Combine(args[2], f.Id + ".pgm"), cell);
        Console.WriteLine(f.Id);
    }
    return 0;
}
if (args.Length > 0 && args[0] == "fields")
{
    var page = Fixtures.Analyze(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(page.AllLines().SelectMany(l => l.Cells.Select(f => new
    {
        f.Id, f.Text, f.Box, f.Ink, Align = f.Align.ToString(), f.Indent, f.FontSize, f.Locked, Kind = l.Kind.ToString()
    })), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return 0;
}
if (args.Length > 0 && args[0] == "dump")
{
    var page = Fixtures.Analyze(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(page.Tables.Select(t => new
    {
        t.Bounds, t.HasGrid, Cols = t.ColumnXs.Select(x => Math.Round(x)), Rows = t.Rows.Count,
        Lines = t.Rows.SelectMany((r, ri) => r.Lines.Select(l => $"r{ri} [{l.Kind}] " + string.Join(" | ", l.Cells.Select(c => c.Text))))
    }), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    foreach (var l in page.LooseLines) Console.WriteLine("hors tableau : " + string.Join(" | ", l.Cells.Select(c => c.Text)));
    return 0;
}
if (args.Length > 0 && args[0] == "export-file")
{
    // Fichier réel (BOM UTF-8, CRLF), exactement comme le bouton « Exporter .txt ».
    var project = new InventoryProject();
    bool withLots = args.Contains("--lots");
    foreach (var name in args.Skip(2).Where(a => a != "--lots")) project.Pages.Add(Fixtures.Analyze(name));
    if (withLots) Lots.Number(project, 1);
    var r = RetoucheInventaire.Core.Export.TxtExporter.Write(project, args[1]);
    Console.WriteLine($"{r.LineCount} lignes, {r.Warnings.Count} avertissement(s)");
    return 0;
}
if (args.Length > 0 && args[0] == "export-frame")
{
    // Page redressée avec la colonne des lots tracée (sans le texte des numéros), pour comparaison avec l'iPad.
    var project = new InventoryProject();
    foreach (var name in args.Skip(2)) project.Pages.Add(Fixtures.Analyze(name));
    Lots.Number(project, 1);
    var page = project.Pages[^1];
    // Une rangée agrandie (texte sur plusieurs lignes) : l'image est étirée avant de tracer la colonne.
    var carton = page.AllLines().First(l => l.Cells[0].Text == "CARTONNAGE");
    PageLayout.SetExtra(page, carton, 37);
    var stretched = PageLayout.Stretch(Fixtures.Load(page.Id).Image, page);
    var framed = RetoucheInventaire.Core.Export.LotColumn.Draw(stretched, page);
    Pgm.Write(args[1], framed);
    foreach (var f in RetoucheInventaire.Core.Export.LotColumn.Frames(stretched, page))
        Console.WriteLine($"x {f.Left}-{f.Right} y {f.Top}-{f.Bottom} traits {f.VThick}/{f.HThick} gris {f.Color[3]}");
    return 0;
}
if (args.Length > 0 && args[0] == "export")
{
    var project = new InventoryProject();
    foreach (var name in args.Skip(1)) project.Pages.Add(Fixtures.Analyze(name));
    Console.Write(RetoucheInventaire.Core.Export.TxtExporter.Build(project).Text);
    return 0;
}

return Tests.RunAll();
