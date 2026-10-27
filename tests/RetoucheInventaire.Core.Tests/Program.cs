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
        Console.WriteLine($"x {f.Left}-{f.Right} rangées {string.Join(",", f.Rows)} traits {f.VThick}/{f.HThick} gris {f.Color[3]}");
    return 0;
}
if (args.Length > 0 && args[0] == "export-arrange")
{
    // Lots 0 recollés après le dernier lot, rangée déplacée, totaux : .txt et pages recomposées, pour comparaison avec l'iPad.
    var project = Tests.ArrangeScenario();
    var plan = RetoucheInventaire.Core.Export.Arrangement.Build(project);
    Totals.Recompute(project, plan);
    Directory.CreateDirectory(args[1]);
    RetoucheInventaire.Core.Export.TxtExporter.Write(project, Path.Combine(args[1], "export.txt"));
    var images = Tests.ComposeAll(project, plan, pg => RetoucheInventaire.Core.Export.LotColumn.Draw(Fixtures.Load(pg.Id).Image, pg));
    for (int i = 0; i < project.Pages.Count; i++)
    {
        Pgm.Write(Path.Combine(args[1], project.Pages[i].Id + ".pgm"), images[i]);
        Console.WriteLine($"{project.Pages[i].Id}: {project.Pages[i].Height} -> {images[i].Height}");
    }
    return 0;
}
if (args.Length > 0 && args[0] == "stress-fixtures")
{
    // Projet de départ du banc d'essai fait des pages de test (pour la CI) : project.json + p1.pgm…
    var project = new InventoryProject();
    foreach (var n in new[] { "page1", "page2", "page5" }) project.Pages.Add(Fixtures.Analyze(n));
    Directory.CreateDirectory(args[1]);
    File.WriteAllText(Path.Combine(args[1], "project.json"), System.Text.Json.JsonSerializer.Serialize(project, RetoucheInventaire.Core.Storage.ProjectStore.Json));
    for (int k = 0; k < project.Pages.Count; k++) Pgm.Write(Path.Combine(args[1], $"p{k + 1}.pgm"), Fixtures.Load(project.Pages[k].Id).Image);
    return 0;
}
if (args.Length > 0 && args[0] == "make-inventaire")
{
    // Projet .inventaire pour l'essai de l'interface Windows (tests/RetoucheInventaire.UiTests) : pages de test,
    // lots créés et synchronisés (lignes rangées par numéro), un lot « VGG » déplacé en haut de la 1re page.
    var project = new InventoryProject { Name = "essai-interface" };
    var sources = new Dictionary<string, byte[]>();
    foreach (var n in new[] { "page1", "page2", "page5" })
    {
        var pg = Fixtures.Analyze(n);
        pg.SourceEntry = $"sources/{n}.jpg";
        sources[pg.SourceEntry] = File.ReadAllBytes(Path.Combine(Fixtures.Dir, n + ".jpg"));
        project.Pages.Add(pg);
    }
    Lots.SetTables(project, project.Pages.SelectMany(pg => Lots.LotTables(pg).Where(x => Lots.DefaultChoice(project, x.Table)).Select(x => x.Table)).ToHashSet());
    Lots.CreateEmpty(project);
    Lots.FillAutomatically(project);
    project.SortLots = true;
    var objs = Lots.ObjectLines(project).Where(o => o.Page == project.Pages[1]).ToList();
    var mover = objs.FirstOrDefault(o => o.Line.FirstText.Contains("autolaveuse"));
    if (mover.Line is null) mover = objs[0];
    Lots.Set(project, mover.Line, "VGG");
    mover.Line.MoveTo = new LotMove { Page = project.Pages[0].Id, Where = "start", Anchor = "" };
    RetoucheInventaire.Core.Storage.ProjectStore.Save(args[1], project, sources);
    Console.WriteLine($"{args[1]} : {project.Pages.Count} pages, {Lots.ObjectLines(project).Count()} objets, « {mover.Line.FirstText} » en VGG en haut de la page 1.");
    return 0;
}
if (args.Length > 0 && args[0] == "plan-dump")
{
    var pj = System.Text.Json.JsonSerializer.Deserialize<RetoucheInventaire.Core.Model.InventoryProject>(File.ReadAllText(args[1]), RetoucheInventaire.Core.Storage.ProjectStore.Json)!;
    var pl = RetoucheInventaire.Core.Export.Arrangement.Build(pj);
    foreach (var u in pl.Moving) Console.WriteLine($"{u.Kind} p{u.P} r{u.R} « {u.Object.FirstText} » -> p{u.Dest.P} t{u.Dest.Ti} r{u.Dest.R}{(u.Dest.Before ? " avant" : "")}");
    return 0;
}
if (args.Length > 0 && args[0] == "lotword") { foreach (var w in args.Skip(1)) Console.WriteLine(w + " -> " + (RetoucheInventaire.Core.Model.Lots.LotWord(w) ?? "null")); return 0; }
if (args.Length > 0 && args[0] == "stress-replay") return Stress.Replay(args[1], args[2], args[3], args[4]);
if (args.Length > 0 && args[0] == "replay")
{
    // Rejoue sur un vrai projet .inventaire la suite d'actions de l'écran Windows (diagnostic).
    var (project, _) = RetoucheInventaire.Core.Storage.ProjectStore.Load(args[1]);
    foreach (var pg in project.Pages) RetoucheInventaire.Core.Analysis.PageAnalyzer.Upgrade(pg);
    void Step(string what, Action a)
    {
        try { a(); var plan = RetoucheInventaire.Core.Export.Arrangement.Build(project); Totals.Recompute(project, plan); for (int k = 0; k < project.Pages.Count; k++) RetoucheInventaire.Core.Export.Arrangement.ViewOf(project, plan, k); RetoucheInventaire.Core.Export.TxtExporter.Build(project); Console.WriteLine("OK   " + what); }
        catch (Exception e) { Console.WriteLine("ERR  " + what + " : " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace); }
    }
    var page = project.Pages[6];
    PrintedLine Find(string t) => page.AllLines().First(l => l.Cells[0].Text.StartsWith(t));
    Lots.SetTables(project, project.Pages.SelectMany(pg => Lots.LotTables(pg).Where(x => Lots.DefaultChoice(project, x.Table)).Select(x => x.Table)).ToHashSet());
    Lots.CreateEmpty(project);
    Step("synchroniser", () => Lots.FillAutomatically(project));
    var rc = Find("1 RICE COOKER");
    Step("4 saisies", () => { rc.Cells[0].Text = "1 RICE COOKER HENDI\n2 Mac book pro\n2 Asus\n2 Acer\n1 souris"; PageLayout.SetExtra(page, rc, 155); Lots.Resync(project); });
    Step("réduire RICE COOKER", () => Console.WriteLine("   réduite ? " + Rows.TrimRow(page, rc)));
    var mac = Find("2 ordinateurs portables MacBook"); var asus = Find("2 ordinateurs portables Asus"); var acer = Find("2 ordinateurs portables Acer");
    Step("séparer sous MacBook", () => { Rows.SplitBelow(page, mac); Rows.TrimRow(page, mac); Rows.TrimRow(page, asus); Lots.Resync(project); });
    Step("séparer sous Asus", () => { Rows.SplitBelow(page, asus); Rows.TrimRow(page, asus); Rows.TrimRow(page, acer); Lots.Resync(project); });
    PrintedLine? blank = null;
    Step("ligne vide sous Acer", () => { Rows.TrimRow(page, acer); blank = Rows.AddBlank(page, acer); Lots.Resync(project); });
    Step("réduire ligne vide", () => Console.WriteLine("   réduite ? " + Rows.TrimRow(page, blank!)));
    Step("lot sur Asus", () => Lots.Set(project, asus, "500"));
    Step("lot sur ligne vide", () => Lots.Set(project, blank!, "501"));
    Step("lot 0 sur Acer", () => Lots.Set(project, acer, "0"));
    Step("synchroniser page", () => Lots.FillAutomatically(project, [page]));
    Console.WriteLine("RICE COOKER : « " + rc.Cells[0].Text.Replace("\n", "⏎") + " » extra " + rc.Extra + " box " + rc.Cells[0].Box);
    foreach (var l in new[] { mac, asus, acer, blank! }) Console.WriteLine($"   [{(l.LotSlot ? l.Lot : "-")}] {l.Cells[0].Text}");
    return 0;
}
if (args.Length > 0 && args[0] == "export-pdf")
{
    // PDF des trois pages de test (comparé au PDF de la version iPad).
    var jpegs = new[] { "page1", "page2", "page5" }.Select(n => File.ReadAllBytes(Path.Combine(Fixtures.Dir, n + ".jpg"))).ToList();
    File.WriteAllBytes(args[1], RetoucheInventaire.Core.Export.PdfWriter.Build(jpegs, "BEENDHI prisé – modifié"));
    return 0;
}
if (args.Length > 0 && args[0] == "export-fin")
{
    // Lots de fin sur une page dédiée, ligne supprimée, ligne TOTAL : .txt et pages composées, pour comparaison avec l'iPad.
    var project = Tests.EndScenario();
    var images = Tests.ComposeEnd(project);
    Directory.CreateDirectory(args[1]);
    RetoucheInventaire.Core.Export.TxtExporter.Write(project, Path.Combine(args[1], "export.txt"));
    for (int i = 0; i < images.Count; i++)
    {
        Pgm.Write(Path.Combine(args[1], i + ".pgm"), images[i]);
        Console.WriteLine($"page {i + 1}: {images[i].Width}x{images[i].Height}");
    }
    return 0;
}
if (args.Length > 0 && args[0] == "export-rows")
{
    // Lignes dupliquées et séparées : .txt et pages agrandies (colonne des lots tracée), pour comparaison avec l'iPad.
    var project = Tests.RowsScenario();
    Directory.CreateDirectory(args[1]);
    RetoucheInventaire.Core.Export.TxtExporter.Write(project, Path.Combine(args[1], "export.txt"));
    foreach (var pg in project.Pages)
    {
        var img = RetoucheInventaire.Core.Export.LotColumn.Draw(PageLayout.Stretch(Fixtures.Load(pg.Id).Image, pg), pg);
        Pgm.Write(Path.Combine(args[1], pg.Id + ".pgm"), img);
        Console.WriteLine($"{pg.Id}: {img.Width}x{img.Height}");
    }
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
