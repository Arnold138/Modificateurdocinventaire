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
if (args.Length > 0 && args[0] == "render-project")
{
    // Pages d'un vrai projet (projet.json + sources converties en .pgm) telles que l'application les compose : pour voir
    // ce que voit le client (rangées, lignes ajoutées, lots déplacés), sans le texte tapé.
    var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(args[1]), RetoucheInventaire.Core.Storage.ProjectStore.Json)!;
    foreach (var pg in project.Pages) PageAnalyzer.Upgrade(pg);
    Lots.Resync(project);
    RetoucheInventaire.Core.Export.Arrangement.SyncEndPages(project);
    var plan = RetoucheInventaire.Core.Export.Arrangement.Build(project);
    if (Rows.AutoCompactMoved(plan)) plan = RetoucheInventaire.Core.Export.Arrangement.Build(project);
    Raster? Flat(Page pg)
    {
        if (pg.EndLots || pg.SourceEntry is null) return null;
        var f = Path.Combine(args[2], Path.GetFileNameWithoutExtension(pg.SourceEntry) + ".pgm");
        if (!File.Exists(f)) return null;
        var oriented = ImageOps.Rotate90(Pgm.Read(f), pg.QuarterTurns);
        var flat = PageNormalizer.Render(oriented, pg.Crop ?? Quad.FullImage(oriented.Width, oriented.Height), pg.Skew, pg.Width, PageLayout.BaseHeight(pg));
        if (pg.Added != null) flat = AddedPage.BlankBase(flat, pg);
        return RetoucheInventaire.Core.Export.LotColumn.Draw(PageLayout.Stretch(flat, pg), pg);
    }
    var drawn = project.Pages.Select(Flat).ToList();
    Directory.CreateDirectory(args[3]);
    for (int k = 0; k < project.Pages.Count; k++)
    {
        var src = drawn[plan.Pages[k].Template ?? k];
        if (src is null) continue;
        var crops = plan.Pages[k].Pieces.Where(pc => pc.IsUnit).ToDictionary(pc => pc.Unit!, pc => (RetoucheInventaire.Core.Export.Arrangement.Crop?)RetoucheInventaire.Core.Export.Arrangement.UnitCrop(pc.Unit!, drawn[pc.Unit!.P]!));
        Pgm.Write(Path.Combine(args[3], $"page{k + 1}.pgm"), RetoucheInventaire.Core.Export.Arrangement.ComposePage(plan, k, src, crops));
        var v = RetoucheInventaire.Core.Export.Arrangement.ViewOf(project, plan, k);
        File.WriteAllLines(Path.Combine(args[3], $"page{k + 1}.txt"), v.Lines.Select(e => $"{(e.Moved ? "D" : " ")} lot={e.Line.Lot,-4} y={e.Cells.FirstOrDefault()?.Box.Y:0} " + string.Join(" | ", e.Cells.Select(c => $"[{c.Box.X:0},{c.Box.Width:0}] {c.Field.Text}"))));
    }
    Console.WriteLine(string.Join(", ", project.Pages.Select((pg, k) => $"p{k + 1} {pg.Height}->{plan.Pages[k].Height}")));
    Console.WriteLine("Contrôle : " + string.Join(" ; ", Integrity.Check(project).Values.Take(20)));
    Lots.SetNarrowing(project);
    Console.WriteLine("Repli des désignations : " + string.Join(", ", project.Pages.Select((pg, k) => (k, n: pg.Tables.SelectMany(t => t.Rows).SelectMany(r => r.Lines).SelectMany(l => l.Cells).Where(c => c.Column == 0).Select(c => c.Narrow).DefaultIfEmpty(0).Max())).Where(x => x.n > 0).Select(x => $"p{x.k + 1} −{x.n}")));
    return 0;
}
if (args.Length > 0 && args[0] == "add-page-real")
{
    // Page ajoutée sur le modèle d'une page d'un vrai projet (avec son image) : colonnes gardées, image vierge.
    var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(args[1]), RetoucheInventaire.Core.Storage.ProjectStore.Json)!;
    var model = project.Pages[int.Parse(args[3]) - 1];
    var oriented = ImageOps.Rotate90(Pgm.Read(Path.Combine(args[2], Path.GetFileNameWithoutExtension(model.SourceEntry!) + ".pgm")), model.QuarterTurns);
    var flat = PageNormalizer.Render(oriented, model.Crop ?? Quad.FullImage(oriented.Width, oriented.Height), model.Skew, model.Width, PageLayout.BaseHeight(model));
    var page = AddedPage.AddBlankPage(project, model, project.Pages.Count, flat, "essai")!;
    Console.WriteLine($"modèle {string.Join(",", model.Tables.Select(t => t.ColumnXs.Count))} colonnes -> page ajoutée {string.Join(",", page.Tables.Select(t => t.ColumnXs.Count))} ; {string.Join(" ", page.Tables[0].ColumnXs.Select(x => Math.Round(x)))}");
    Pgm.Write(args[4], AddedPage.BlankBase(flat, page));
    return 0;
}
if (args.Length > 0 && args[0] == "repair-real")
{
    // Réparation à l'ouverture d'un vrai projet (comme l'application) : pages relues, pages vierges recréées ; projet réparé écrit.
    var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(args[1]), RetoucheInventaire.Core.Storage.ProjectStore.Json)!;
    foreach (var pg in project.Pages) PageAnalyzer.Upgrade(pg);
    Lots.Resync(project);
    var before = Integrity.Check(project);
    Raster Flat(Page pg)
    {
        var oriented = ImageOps.Rotate90(Pgm.Read(Path.Combine(args[2], Path.GetFileNameWithoutExtension(pg.SourceEntry!) + ".pgm")), pg.QuarterTurns);
        return PageNormalizer.Render(oriented, pg.Crop ?? Quad.FullImage(oriented.Width, oriented.Height), pg.Skew, pg.Width, PageLayout.BaseHeight(pg));
    }
    for (int k = 0; k < project.Pages.Count; k++)
    {
        var pg = project.Pages[k];
        if (!PageRepair.Pending(pg)) continue;
        string was = string.Join("+", pg.Tables.Select(t => $"{t.ColumnXs.Count}c{t.Rows.Count}r"));
        bool changed = PageRepair.Regrid(pg, Flat(pg));
        if (changed) Console.WriteLine($"page {k + 1} relue : {was} -> {string.Join("+", pg.Tables.Select(t => $"{t.ColumnXs.Count}c{t.Rows.Count}r"))}");
    }
    for (int k = 0; k < project.Pages.Count; k++)
    {
        var pg = project.Pages[k];
        if (PageRepair.BrokenBlank(project, pg) is not { } model) continue;
        project.Pages.RemoveAt(k);
        var fresh = AddedPage.AddBlankPage(project, model, k, Flat(model), pg.Id)!;
        Console.WriteLine($"page {k + 1} recréée sur la page {project.Pages.IndexOf(model) + 1} : {string.Join("+", fresh.Tables.Select(t => $"{t.ColumnXs.Count}c{t.Rows.Count}r"))}");
    }
    Lots.Resync(project);
    var after = Integrity.Check(project);
    Console.WriteLine("Nouveaux défauts : " + string.Join(" ; ", Integrity.NewProblems(before, after)));
    File.WriteAllText(args[3], JsonSerializer.Serialize(project, RetoucheInventaire.Core.Storage.ProjectStore.Json));
    return 0;
}
if (args.Length > 0 && args[0] == "scenario-real")
{
    // Manipulations du client du 13/11 sur son vrai fichier : supprimer d'un coup les 20 lignes ajoutées, en remettre 20 sous
    // « 3 tabourets », 20 sous la ligne barrée de LABOTEST, puis Synchroniser (numéro de lot trop long). Contrôle à chaque pas.
    var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(args[1]), RetoucheInventaire.Core.Storage.ProjectStore.Json)!;
    foreach (var pg in project.Pages) PageAnalyzer.Upgrade(pg);
    Lots.Resync(project);
    var start = Integrity.Check(project);
    void Step(string what)
    {
        Lots.Resync(project);
        Lots.SetNarrowing(project);
        var plan = RetoucheInventaire.Core.Export.Arrangement.Build(project);
        if (Rows.AutoCompactMoved(plan)) RetoucheInventaire.Core.Export.Arrangement.Build(project);
        var bad = Integrity.NewProblems(start, Integrity.Check(project));
        Console.WriteLine($"{what} : {(bad.Count == 0 ? "mise en page saine" : "PROBLÈMES " + string.Join(" ; ", bad))}");
    }
    Raster Flat(Page pg)
    {
        var oriented = ImageOps.Rotate90(Pgm.Read(Path.Combine(args[2], Path.GetFileNameWithoutExtension(pg.SourceEntry!) + ".pgm")), pg.QuarterTurns);
        return PageNormalizer.Render(oriented, pg.Crop ?? Quad.FullImage(oriented.Width, oriented.Height), pg.Skew, pg.Width, PageLayout.BaseHeight(pg));
    }
    var p10 = project.Pages[9];
    int h0 = p10.Height;
    var added = p10.Tables.SelectMany(t => t.Rows).Where(r => r.Inserted != null).SelectMany(r => r.Lines).ToList();
    int removed = 0;
    foreach (var l in Enumerable.Reverse(added)) if (Rows.RemoveAdded(p10, l)) removed++;
    Rows.ForgetTrims(p10);
    Console.WriteLine($"page 10 : {removed}/{added.Count} lignes ajoutées supprimées d'un coup, hauteur {h0} -> {p10.Height}, rangées ajoutées restantes {p10.Tables.SelectMany(t => t.Rows).Count(r => r.Inserted != null)}");
    Step("suppression");
    PrintedLine Find(string text) => p10.AllLines().First(l => l.Cells.Any(c => c.Text.Contains(text)));
    var img10 = Flat(p10);
    var r1 = AddedPage.AddRows(project, p10, Find("tabourets"), 20, img: img10);
    Console.WriteLine($"20 lignes sous « 3 tabourets » : {r1.Added} ajoutées, autres pages {r1.Pages.Count}");
    Step("ajout 1");
    var r2 = AddedPage.AddRows(project, p10, Find("ABOTEST"), 20, img: img10);
    Console.WriteLine($"20 lignes sous « LABOTEST » (ligne barrée dessous) : {r2.Added} ajoutées");
    Step("ajout 2");
    var r3 = AddedPage.AddRows(project, p10, Find("ABOTEST"), 20, above: true, img: img10);
    Console.WriteLine($"20 lignes au-dessus de la même : {r3.Added} ajoutées");
    Step("ajout 3");
    Lots.FillAutomatically(project);
    Step("Synchroniser (lot « 17252252252 » présent : " + project.Pages.SelectMany(p => p.AllLines()).Any(l => l.Lot == "17252252252") + ")");
    File.WriteAllText(args[3], JsonSerializer.Serialize(project, RetoucheInventaire.Core.Storage.ProjectStore.Json));
    return 0;
}
if (args.Length > 0 && args[0] == "stress-fixtures")
{
    // Projet de départ du banc d'essai fait des pages de test (pour la CI) : project.json + p1.pgm…
    var project = new InventoryProject();
    foreach (var n in args.Length > 2 ? args.Skip(2).ToArray() : new[] { "page1", "page2", "page5" }) project.Pages.Add(Fixtures.Analyze(n));
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
if (args.Length > 0 && args[0] == "stress-scenario") return Stress.Scenario(args[1], args[2], args[3]);
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
