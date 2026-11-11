using System.Text.Json;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Export;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Storage;

namespace RetoucheInventaire.Core.Tests;

/// <summary>
/// Rejoue la liste de manipulations du banc d'essai iPad (web/tests/stress.mjs, ops.json) avec le cœur Windows, comme
/// l'écran Windows les fait (réductions automatiques, cases de lot, mise en page, totaux), contrôle la mise en page
/// après chaque pas, puis écrit le .txt et les pages composées pour les comparer à l'octet près avec la version iPad.
/// </summary>
public static class Stress
{
    public static int Replay(string opsPath, string projectPath, string imagesDir, string outDir)
    {
        var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(projectPath), ProjectStore.Json)!;
        foreach (var p in project.Pages) PageAnalyzer.Upgrade(p);
        var images = project.Pages.Select((p, k) => (p.Id, Img: Pgm.Read(Path.Combine(imagesDir, $"p{k + 1}.pgm")))).ToDictionary(x => x.Id, x => x.Img);
        var baseH = project.Pages.ToDictionary(p => p.Id, PageLayout.BaseHeight);
        _raw = new Dictionary<string, Raster>(images);
        _baseH = baseH;
        var problems = new List<string>();
        void Fail(int step, string what) { if (problems.Count < 200) problems.Add($"#{step} {what}"); }

        Lots.SetTables(project, project.Pages.SelectMany(pg => Lots.LotTables(pg).Where(x => Lots.DefaultChoice(project, x.Table)).Select(x => x.Table)).ToHashSet());
        Lots.CreateEmpty(project);
        Lots.FillAutomatically(project);
        After(project);
        // Comme à l'écran : la page s'affiche (colonne des lots tracée) avant toute manipulation.
        foreach (var pg in project.Pages.Where(pg => !pg.EndLots)) LotColumn.Draw(PageLayout.Stretch(images[pg.Id], pg), pg);

        // Le contrôle que fait l'application après chaque action (retour automatique à l'état d'avant s'il trouve un problème).
        var sound = Integrity.Check(project);

        using var doc = JsonDocument.Parse(File.ReadAllText(opsPath));
        int step = 0;
        foreach (var o in doc.RootElement.GetProperty("ops").EnumerateArray())
        {
            step++;
            string op = o.GetProperty("op").GetString()!;
            var t = o.GetProperty("t");
            var a = o.GetProperty("a");
            try { Run(project, op, t.GetProperty("p").GetInt32(), t.GetProperty("ti").GetInt32(), t.GetProperty("o").GetInt32(), t.GetProperty("i").GetInt32(), a); }
            catch (Exception e) { Fail(step, $"{op} : ERREUR {e.GetType().Name} {e.Message}\n{e.StackTrace?.Split('\n').Take(3).Aggregate((x, y) => x + "\n" + y)}"); }
            try
            {
                var plan = After(project);
                Check(project, plan, baseH, step, op, Fail);
                foreach (var x in Integrity.NewProblems(sound, Integrity.Check(project))) Fail(step, $"{op} : contrôle de l'application : {x}");
                // Fermer puis rouvrir l'application (ou revenir en arrière) ne doit rien changer au projet.
                if (Reopen(project) is { } diff) Fail(step, $"{op} : le projet change quand on le rouvre ({diff})");
            }
            catch (Exception e) { Fail(step, $"{op} (après) : ERREUR {e.GetType().Name} {e.Message}\n{e.StackTrace?.Split('\n').Take(3).Aggregate((x, y) => x + "\n" + y)}"); }
        }

        var final = After(project);
        Directory.CreateDirectory(outDir);
        if (Environment.GetEnvironmentVariable("SAVE") != null) File.WriteAllText(Path.Combine(outDir, "project.json"), JsonSerializer.Serialize(project, ProjectStore.Json));
        File.WriteAllText(Path.Combine(outDir, "export.txt"), TxtExporter.Build(project).Text);
        var drawn = project.Pages.Select(pg => pg.EndLots ? null : LotColumn.Draw(LotColumn.CloseTables(PageLayout.Stretch(ImageOf(images, pg), pg), pg), pg)).ToList();
        for (int k = 0; k < project.Pages.Count; k++)
        {
            var src = drawn[final.Pages[k].Template ?? k];
            if (src is null) continue;
            var crops = final.Pages[k].Pieces.Where(pc => pc.IsUnit).ToDictionary(pc => pc.Unit!, pc => (Arrangement.Crop?)Arrangement.UnitCrop(pc.Unit!, drawn[pc.Unit!.P]!));
            var img = Arrangement.ComposePage(final, k, src, crops);
            if (img.Height != final.Pages[k].Height) Fail(step, $"page {k + 1} : image {img.Height} ≠ plan {final.Pages[k].Height}");
            Pgm.Write(Path.Combine(outDir, $"page{k + 1}.pgm"), img);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) Integrity.Check(project);
        long check = sw.ElapsedMilliseconds / 5;
        sw.Restart();
        string snap = JsonSerializer.Serialize(project, ProjectStore.Json);
        Console.WriteLine($"Contrôle de l'application : {check} ms ; état du projet : {snap.Length / 1024} Ko, {sw.ElapsedMilliseconds} ms.");
        Console.WriteLine($"Windows : {step} manipulations rejouées, {project.Pages.Count} pages, {Lots.ObjectLines(project).Count()} objets.");
        Console.WriteLine(problems.Count == 0 ? "Aucun problème." : $"{problems.Count} problème(s) :\n" + string.Join("\n", problems.Take(30)));
        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>Enregistre et rouvre le projet comme l'application (copie de secours, ↶) : null s'il revient identique.</summary>
    public static string? Reopen(InventoryProject project)
    {
        string before = JsonSerializer.Serialize(project, ProjectStore.Json);
        var again = JsonSerializer.Deserialize<InventoryProject>(before, ProjectStore.Json)!;
        foreach (var p in again.Pages) PageAnalyzer.Upgrade(p);
        Lots.Resync(again);
        string after = JsonSerializer.Serialize(again, ProjectStore.Json);
        if (after == before) return null;
        var x = before.Split('\n'); var y = after.Split('\n');
        int i = 0;
        while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
        return $"ligne {i + 1} : « {(i < x.Length ? x[i].Trim() : "fin")} » devient « {(i < y.Length ? y[i].Trim() : "fin")} », {x.Length} → {y.Length} lignes";
    }

    /// <summary>
    /// Scénario « client » : sur chaque page à lots d'un vrai inventaire, ce qu'un utilisateur fait pendant une longue séance.
    /// Six ou sept lignes de description tapées sous un lot sur deux, dix lignes vides ajoutées sous un lot et remplies,
    /// texte tapé séparé en rangées, numéros de lot tapés, cases de lot ajoutées sur des lignes de suite, lots mis à 0 et
    /// replacés, rangées et lignes déplacées, « Synchroniser » ; et, toutes les quatre manipulations, un retour en arrière (↶)
    /// qui doit rendre exactement l'état d'avant. Après chaque pas : mise en page contrôlée, projet fermé puis rouvert.
    /// </summary>
    public static int Scenario(string projectPath, string imagesDir, string outDir)
    {
        var project = JsonSerializer.Deserialize<InventoryProject>(File.ReadAllText(projectPath), ProjectStore.Json)!;
        foreach (var p in project.Pages) PageAnalyzer.Upgrade(p);
        var images = project.Pages.Select((p, k) => (p.Id, Img: Pgm.Read(Path.Combine(imagesDir, $"p{k + 1}.pgm")))).ToDictionary(x => x.Id, x => x.Img);
        var baseH = project.Pages.ToDictionary(p => p.Id, PageLayout.BaseHeight);
        _raw = new Dictionary<string, Raster>(images);
        _baseH = baseH;
        var problems = new List<string>();
        int step = 0, undone = 0;
        void Fail(int n, string what) { if (problems.Count < 200) problems.Add($"#{n} {what}"); }

        Lots.SetTables(project, project.Pages.SelectMany(pg => Lots.LotTables(pg).Where(x => Lots.DefaultChoice(project, x.Table)).Select(x => x.Table)).ToHashSet());
        Lots.CreateEmpty(project);
        Lots.FillAutomatically(project);
        After(project);
        var sound = Integrity.Check(project);
        var counts = new SortedDictionary<string, int>();

        // Une manipulation, comme à l'écran : contrôles après, et un retour en arrière une fois sur quatre (puis refaite).
        void Do(string op, int p, int ti, int o, int i, string args = "{}")
        {
            using var a = JsonDocument.Parse(args);
            string before = JsonSerializer.Serialize(project, ProjectStore.Json);
            for (int pass = 0; pass < 2; pass++)
            {
                step++;
                counts[op] = counts.GetValueOrDefault(op) + 1;
                try { Run(project, op, p, ti, o, i, a.RootElement); }
                catch (Exception e) { Fail(step, $"{op} : ERREUR {e.GetType().Name} {e.Message}"); }
                try
                {
                    var plan = After(project);
                    Check(project, plan, _baseH, step, op, Fail);
                    foreach (var x in Integrity.NewProblems(sound, Integrity.Check(project))) Fail(step, $"{op} : contrôle de l'application : {x}");
                    if (Reopen(project) is { } diff) Fail(step, $"{op} : le projet change quand on le rouvre ({diff})");
                }
                catch (Exception e) { Fail(step, $"{op} (après) : ERREUR {e.GetType().Name} {e.Message}"); }
                if (pass == 1 || step % 4 != 0) break;
                // ↶ : l'état d'avant revient exactement (comme RestoreAsync), puis la manipulation est refaite.
                project = JsonSerializer.Deserialize<InventoryProject>(before, ProjectStore.Json)!;
                After(project);
                undone++;
                if (JsonSerializer.Serialize(project, ProjectStore.Json) != before) Fail(step, $"{op} : ↶ ne rend pas l'état d'avant");
            }
        }

        List<(int Ti, int O, int I, PrintedLine Line)> Objects(int p) =>
            project.Pages[p].Tables.SelectMany((t, ti) => !t.HasGrid || t.LotsOff || t.RowYs.Count < 2 ? [] :
                Lots.RowOrder(t).SelectMany((r, o) => t.Rows[r].Lines.Select((l, i) => (Ti: ti, O: o, I: i, Line: l))))
            .Where(x => Lots.HasCell(x.Line) && !x.Line.Deleted && x.Line.FirstText.Length > 0 && Rows.CanCopy(project.Pages[p], x.Line)).ToList();
        // Les lignes sont suivies par l'identifiant de leur 1re case : après un ↶, le projet est un autre objet.
        (int Ti, int O, int I)? Find(int p, PrintedLine? held)
        {
            string? key = held?.Cells.FirstOrDefault()?.Id;
            if (string.IsNullOrEmpty(key)) return null;
            var pg = project.Pages[p];
            for (int ti = 0; ti < pg.Tables.Count; ti++)
            {
                var t = pg.Tables[ti];
                var order = Lots.RowOrder(t);
                for (int o = 0; o < order.Count; o++) { int i = t.Rows[order[o]].Lines.FindIndex(l => l.Cells.FirstOrDefault()?.Id == key); if (i >= 0) return (ti, o, i); }
            }
            return null;
        }
        string Lines(int n, string what) => "{\"lines\":[" + string.Join(",", Enumerable.Range(1, n).Select(k => $"\"{what} ligne {k} de description ajoutée\"")) + "]}";

        int lot = 500;
        // Vidéo 3 du client : « une centaine de lignes à taper » : Ajouter des lignes (100, 50, 10, 1) sous le dernier objet de
        // chaque page, puis écrire dans ces lignes (désignation, prix, numéro de lot), Synchroniser, une ligne au-dessus.
        if (Environment.GetEnvironmentVariable("LIGNES") is { Length: > 0 })
        {
            int[] sizes = [100, 50, 10, 1, 15, 5];
            int nPage = 0, written = 0;
            var lotPages = project.Pages.Where(pg => !pg.EndLots && pg.Added == null).Select(pg => pg.Id).ToList();
            (int P, int Ti, int O, int I)? FindAny(PrintedLine held)
            {
                for (int q = 0; q < project.Pages.Count; q++) if (Find(q, held) is { } at) return (q, at.Ti, at.O, at.I);
                return null;
            }
            foreach (var id in lotPages)
            {
                int p = project.Pages.FindIndex(pg => pg.Id == id);
                if (p < 0 || Objects(p).Count == 0) continue;
                var obj = Objects(p)[^1].Line;
                int n = sizes[nPage++ % sizes.Length];
                if (Find(p, obj) is { } at) Do("addRows", p, at.Ti, at.O, at.I, $"{{\"n\":{n},\"above\":false,\"id\":\"cent{p}\"}}");
                // Lignes vides ajoutées (sur la page et les pages blanches qui suivent) : on écrit dans 25 d'entre elles.
                var empty = project.Pages.SelectMany(pg => pg.Tables.SelectMany(t => t.Rows.Where(r => r.Inserted != null || pg.Added != null).SelectMany(r => r.Lines)))
                    .Where(l => l.FirstText.Length == 0 && !l.Deleted && l.Kind == LineKind.Article && l.Cells.Count > 1).Take(25).ToList();
                foreach (var l in empty)
                {
                    if (FindAny(l) is not { } w) continue;
                    Do("value", w.P, w.Ti, w.O, w.I, $"{{\"c\":0,\"v\":\"{1 + written % 4} objet tapé {written}\"}}");
                    if (FindAny(l) is { } w2) Do("value", w2.P, w2.Ti, w2.O, w2.I, $"{{\"c\":{l.Cells.Count - 1},\"v\":\"{10 * (1 + written % 9)}\"}}");
                    if (written % 3 == 0 && FindAny(l) is { } w3) Do("addLot", w3.P, w3.Ti, w3.O, w3.I);
                    if (written % 3 == 0 && FindAny(l) is { } w4) Do("lot", w4.P, w4.Ti, w4.O, w4.I, $"{{\"v\":\"{lot++}\"}}");
                    if (written % 7 == 0 && FindAny(l) is { } w5) Do("type", w5.P, w5.Ti, w5.O, w5.I, "{\"lines\":[\"suite de la description\",\"encore une ligne\"]}");
                    written++;
                }
                if (Find(p, obj) is { } b0) Do("blank", p, b0.Ti, b0.O, b0.I, "{\"above\":true,\"text\":\"au-dessus du dernier objet\"}");
                if (FindAny(obj) is { } s0) Do("sync", s0.P, s0.Ti, s0.O, s0.I, $"{{\"page\":{s0.P}}}");
            }
        }
        for (int p = 0; p < project.Pages.Count && Environment.GetEnvironmentVariable("LIGNES") is not { Length: > 0 }; p++)
        {
            if (project.Pages[p].EndLots || Objects(p).Count == 0) continue;
            string id = project.Pages[p].Id;
            // 1) Six ou sept lignes de description sous un lot sur deux (au plus douze par page).
            int n = 0;
            foreach (var x in Objects(p).Where((_, k) => k % 2 == 0).Take(12).Select(x => x.Line).ToList())
                if (Find(p, x) is { } at) Do("type", p, at.Ti, at.O, at.I, Lines(6 + n++ % 2, id));
            // 2) Dix lignes vides sous le premier lot, remplies ; une sur trois agrandie à son tour.
            if (Objects(p).FirstOrDefault().Line is { } first)
                for (int k = 0; k < 10; k++)
                {
                    if (Find(p, first) is not { } at) break;
                    Do("blank", p, at.Ti, at.O, at.I, $"{{\"above\":false,\"text\":\"{id} ligne ajoutée {k + 1}\"}}");
                    if (k % 3 == 0 && Find(p, first) is { } again && again.O + 1 < Lots.RowOrder(project.Pages[p].Tables[again.Ti]).Count)
                        Do("type", p, again.Ti, again.O + 1, 0, Lines(3, id + " suite"));
                }
            // 3) Texte tapé séparé en rangées (« Séparer ici » dans un texte de plusieurs lignes).
            foreach (var x in Objects(p).Where(x => x.Line.Cells[0].Text.Split('\n').Length > 4).Take(3).Select(x => x.Line).ToList())
                if (Find(p, x) is { } at) Do("typed", p, at.Ti, at.O, at.I, "{\"from\":3}");
            // 4) Numéros tapés, « 12 bis », case de lot ajoutée sur une ligne de suite, lot 0, puis retour.
            foreach (var x in Objects(p).Take(8).Select(x => x.Line).ToList())
                if (Find(p, x) is { } at) Do("lot", p, at.Ti, at.O, at.I, $"{{\"v\":\"{(lot % 7 == 0 ? lot + " bis" : lot.ToString())}\"}}") ; else lot++;
            lot += 10;
            var suite = project.Pages[p].Tables.SelectMany(t => t.HasGrid && !t.LotsOff ? t.Rows.SelectMany(r => r.Lines.Skip(1)) : [])
                .Where(l => !Lots.HasCell(l) && l.Kind == LineKind.Article && l.FirstText.Length > 0).Take(3).ToList();
            foreach (var l in suite) if (Find(p, l) is { } at) { Do("addLot", p, at.Ti, at.O, at.I); Do("lot", p, at.Ti, at.O, at.I, $"{{\"v\":\"{lot++}\"}}"); }
            var objs = Objects(p);
            if (objs.Count > 3)
            {
                var z = objs[objs.Count / 2].Line;
                if (Find(p, z) is { } at) Do("lot", p, at.Ti, at.O, at.I, "{\"v\":\"0\"}");
                if (Find(p, z) is { } at2) Do("placeZero", p, at2.Ti, at2.O, at2.I, "{\"to\":1}");
                if (Find(p, z) is { } at3) Do("lot", p, at3.Ti, at3.O, at3.I, $"{{\"v\":\"{lot++}\"}}");
            }
            // 5) Rangées et lignes déplacées (un lot monte, un autre descend ; une ligne sort de son bloc).
            foreach (var (x, to) in Objects(p).Where((_, k) => k % 5 == 1).Take(4).Select((x, k) => (x.Line, k * 3 + 1)).ToList())
                if (Find(p, x) is { } at) Do("moveRow", p, at.Ti, at.O, at.I, $"{{\"to\":{to}}}");
            var inBlock = project.Pages[p].Tables.SelectMany(t => t.HasGrid ? t.Rows.Where(r => r.Lines.Count(l => l.FirstText.Length > 0) > 2).Select(r => r.Lines[1]) : [])
                .Where(l => Rows.CanSplit(project.Pages[p], l)).Take(2).ToList();
            foreach (var l in inBlock) if (Find(p, l) is { } at) Do("moveLine", p, at.Ti, at.O, at.I, "{\"to\":0}");
            // 7) Vidéo 2 : lignes vides ajoutées au milieu des blocs (au-dessus et en dessous de lignes intérieures), remplies,
            //    puis « Séparer ici » sur d'autres lignes du bloc ; et une rangée de 5 lignes au milieu d'un bloc.
            var pgx = project.Pages[p];
            var inner = pgx.Tables.SelectMany(t => t.HasGrid && t.RowYs.Count >= 2 ? t.Rows.Where(r => r.Lines.Count >= 3).SelectMany(r => r.Lines.Skip(1)) : [])
                .Where(l => Rows.CanCopy(pgx, l) && !l.Deleted).Take(8).ToList();
            for (int k = 0; k < inner.Count; k++)
            {
                if (Find(p, inner[k]) is not { } at) continue;
                if (k % 3 == 2) Do("split", p, at.Ti, at.O, at.I);
                else Do("blank", p, at.Ti, at.O, at.I, $"{{\"above\":{(k % 3 == 1 ? "true" : "false")},\"text\":\"{id} ajout dans le bloc {k}\"}}");
            }
            if (inner.Count > 0 && Find(p, inner[^1]) is { } ar) Do("addRows", p, ar.Ti, ar.O, ar.I, "{\"n\":5,\"above\":false,\"id\":\"lr" + p + "\"}");
            // 6) Synchroniser cette page, puis une ligne TOTAL.
            if (Objects(p).FirstOrDefault().Line is { } any && Find(p, any) is { } s0) Do("sync", p, s0.Ti, s0.O, s0.I, $"{{\"page\":{p}}}");
            if (Objects(p).LastOrDefault().Line is { } last && Find(p, last) is { } t0) Do("total", p, t0.Ti, t0.O, t0.I, "{\"mode\":\"page\",\"bold\":true}");
        }

        var final = After(project);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "export.txt"), TxtExporter.Build(project).Text);
        var drawn = project.Pages.Select(pg => pg.EndLots ? null : LotColumn.Draw(PageLayout.Stretch(ImageOf(images, pg), pg), pg)).ToList();
        for (int k = 0; k < project.Pages.Count; k++)
        {
            var src = drawn[final.Pages[k].Template ?? k];
            if (src is null) continue;
            var crops = final.Pages[k].Pieces.Where(pc => pc.IsUnit).ToDictionary(pc => pc.Unit!, pc => (Arrangement.Crop?)Arrangement.UnitCrop(pc.Unit!, drawn[pc.Unit!.P]!));
            var img = Arrangement.ComposePage(final, k, src, crops);
            if (img.Height != final.Pages[k].Height) Fail(step, $"page {k + 1} : image {img.Height} ≠ plan {final.Pages[k].Height}");
            Pgm.Write(Path.Combine(outDir, $"page{k + 1}.pgm"), img);
        }
        File.WriteAllText(Path.Combine(outDir, "project.json"), JsonSerializer.Serialize(project, ProjectStore.Json));
        Console.WriteLine($"Scénario client : {step} manipulations ({string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}), {undone} retours en arrière, {project.Pages.Count} pages, {Lots.ObjectLines(project).Count()} objets.");
        Console.WriteLine("Hauteurs des pages : " + string.Join(", ", project.Pages.Select((pg, k) => $"p{k + 1} {final.Pages[k].Height}")));
        Console.WriteLine(problems.Count == 0 ? "Aucun problème." : $"{problems.Count} problème(s) :\n" + string.Join("\n", problems.Take(30)));
        return problems.Count == 0 ? 0 : 1;
    }

    private static Arrangement.Plan After(InventoryProject project)
    {
        Lots.Resync(project);
        Arrangement.SyncEndPages(project);
        var plan = Arrangement.Build(project);
        // Comme l'application : lignes rangées ou déplacées resserrées une fois.
        if (Rows.AutoCompactMoved(plan)) plan = Arrangement.Build(project);
        Totals.Recompute(project, plan);
        return plan;
    }

    private static int ExtraOf(PrintedLine line) =>
        Math.Max(PageLayout.TallExtra(line), line.Cells.Where(f => f.Text.Contains('\n') && !f.Locked).Select(f => PageLayout.ExtraFor(line, f, f.Text.Split('\n').Length, f.FontSize)).DefaultIfEmpty(0).Max());

    // Pages ajoutées : image de la source du modèle, redessinée vierge (comme dans l'application).
    private static Dictionary<string, Raster> _raw = [];
    private static Dictionary<string, int> _baseH = [];
    private static Raster ImageOf(Dictionary<string, Raster> images, Page pg) => pg.Added != null ? AddedPage.BlankBase(_raw[pg.Id], pg) : images[pg.Id];

    private static void Run(InventoryProject project, string op, int p, int ti, int o, int i, JsonElement a)
    {
        var pages = project.Pages;
        var page = pages[p];
        var table = page.Tables[ti];
        int r = Lots.RowOrder(table)[o];
        var row = table.Rows[r];
        var line = row.Lines[i];
        switch (op)
        {
            case "type":
                line.Cells[0].Text += string.Concat(a.GetProperty("lines").EnumerateArray().Select(w => "\n" + w.GetString()));
                PageLayout.SetExtra(page, line, ExtraOf(line));
                break;
            case "addWord":
                line.Cells[0].Text = line.Cells[0].Text.TrimEnd() + " " + a.GetProperty("w").GetString();
                PageLayout.SetExtra(page, line, ExtraOf(line));
                break;
            case "removeWord":
            {
                var t = Regex.Replace(line.Cells[0].Text, $@"(?<![A-Za-z0-9_]){Regex.Escape(a.GetProperty("w").GetString()!)}(?![A-Za-z0-9_])", "");
                t = Regex.Replace(t, " {2,}", " ");
                t = Regex.Replace(t, @"\n\s*\n", "\n");
                line.Cells[0].Text = t.Trim();
                PageLayout.SetExtra(page, line, ExtraOf(line));
                break;
            }
            case "value":
            {
                int c = a.GetProperty("c").GetInt32();
                if (c < line.Cells.Count) line.Cells[c].Text = a.GetProperty("v").GetString()!;
                break;
            }
            case "typed":
            {
                if (Rows.CanCopy(page, line)) Rows.TrimRow(page, line);
                var show = Rows.SplitTypedText(page, line, a.GetProperty("from").GetInt32());
                if (show != null) { PageLayout.SetExtra(page, line, ExtraOf(line)); PageLayout.SetExtra(page, show, ExtraOf(show)); }
                break;
            }
            case "split":
            {
                var at = Rows.Locate(page, line);
                var prev = at is { I: > 0 } ? at.Row.Lines[at.I - 1] : null;
                if (Rows.Split(page, line) && prev != null) { Rows.TrimRow(page, prev); Rows.TrimRow(page, line); }
                break;
            }
            case "splitBelow":
            {
                var at = Rows.Locate(page, line);
                var next = at != null && at.I + 1 < at.Row.Lines.Count ? at.Row.Lines[at.I + 1] : null;
                if (Rows.SplitBelow(page, line) && next != null) { Rows.TrimRow(page, line); Rows.TrimRow(page, next); }
                break;
            }
            case "copy":
            {
                if (Rows.CanCopy(page, line)) Rows.TrimRow(page, line);
                var c = Rows.Copy(page, line);
                if (c != null) PageLayout.SetExtra(page, c, ExtraOf(c));
                break;
            }
            case "blank":
            {
                if (Rows.CanCopy(page, line)) Rows.TrimRow(page, line);
                var b = Rows.AddBlank(page, line, a.GetProperty("above").GetBoolean());
                string text = a.GetProperty("text").GetString()!;
                if (b != null && text.Length > 0) b.Cells[0].Text = text;
                break;
            }
            case "removeAdded": Rows.RemoveAdded(page, line); break;
            case "addPage":
            {
                bool empty = a.TryGetProperty("empty", out var e) && e.GetBoolean();
                var pg = AddedPage.AddBlankPage(project, page, a.GetProperty("at").GetInt32(), _raw[page.Id], a.GetProperty("id").GetString(), empty);
                if (pg != null) { _raw[pg.Id] = _raw[page.Id]; _baseH[pg.Id] = PageLayout.BaseHeight(pg); }
                break;
            }
            case "addRows":
            {
                var res = AddedPage.AddRows(project, page, line, a.GetProperty("n").GetInt32(), a.GetProperty("above").GetBoolean(), _raw[page.Id], a.GetProperty("id").GetString());
                foreach (var pg in res.Pages) { _raw[pg.Id] = _raw[page.Id]; _baseH[pg.Id] = PageLayout.BaseHeight(pg); }
                break;
            }
            case "removePage": if (page.Added != null) pages.RemoveAt(p); break;
            case "fill":
            {
                line.Cells[0].Text = "1 objet " + a.GetProperty("w").GetString();
                var v = a.GetProperty("v").EnumerateArray().Select(x => x.GetString()!).ToList();
                for (int c = 1; c < line.Cells.Count; c++) line.Cells[c].Text = c - 1 < v.Count ? v[c - 1] : "";
                break;
            }
            case "addCol": AddedPage.AddColumn(page, table, a.GetProperty("c").GetInt32()); break;
            case "removeCol": AddedPage.RemoveColumn(page, table, a.GetProperty("k").GetInt32()); break;
            case "taller": line.Tall++; PageLayout.SetExtra(page, line, ExtraOf(line)); break;
            case "shorter": line.Tall = Math.Max(0, line.Tall - 1); PageLayout.SetExtra(page, line, ExtraOf(line)); break;
            case "del": line.Deleted = true; break;
            case "restore": line.Deleted = false; break;
            case "lot": Lots.Set(project, line, a.GetProperty("v").GetString()!); break;
            case "addLot": Lots.Add(project, line); break;
            case "addLotHere": Lots.AddHere(project, table, line); break;
            case "moveLot":
            {
                string anchor = "";
                if (a.GetProperty("anchor") is { ValueKind: JsonValueKind.Object } an)
                {
                    var at = pages[an.GetProperty("p").GetInt32()].Tables[an.GetProperty("ti").GetInt32()];
                    var al = at.Rows[Lots.RowOrder(at)[an.GetProperty("o").GetInt32()]].Lines[an.GetProperty("i").GetInt32()];
                    anchor = al.Cells.Count > 0 ? al.Cells[0].Id : "";
                }
                line.MoveTo = new LotMove { Page = pages[a.GetProperty("page").GetInt32()].Id, Where = a.GetProperty("where").GetString()!, Anchor = anchor };
                break;
            }
            case "unmoveLot": line.MoveTo = null; break;
            case "sortLots": project.SortLots = a.GetProperty("on").GetBoolean(); break;
            case "align": line.LotPos = null; line.LotAlign = a.GetProperty("v").ValueKind == JsonValueKind.Null ? null : a.GetProperty("v").GetString(); break;
            case "frameToggle": foreach (var x in a.GetProperty("rows").EnumerateArray()) table.Rows[x.GetInt32()].LotFrame = a.GetProperty("on").GetBoolean(); break;
            case "sync":
            {
                var pg = a.GetProperty("page");
                Lots.FillAutomatically(project, pg.ValueKind == JsonValueKind.Null ? null : new List<Page> { pages[pg.GetInt32()] });
                break;
            }
            case "syncApp":
            {
                // Bouton « Synchroniser » de l'application : remplit, puis range les lignes par numéro (et le garde).
                var pg = a.GetProperty("page");
                Lots.FillAutomatically(project, pg.ValueKind == JsonValueKind.Null ? null : new List<Page> { pages[pg.GetInt32()] });
                project.SortLots = true;
                break;
            }
            case "dragRow":
            {
                // Poignée ⋮⋮ d'une rangée (MainWindow.RowMoved) : lignes triées → posée à la main, sinon ordre des rangées.
                int? before = a.GetProperty("before").ValueKind == JsonValueKind.Null ? null : a.GetProperty("before").GetInt32();
                var numbered = row.Lines.Where(l => Lots.HasCell(l) && l.Lot.Trim().Length > 0 && !Lots.IsZero(l)).ToList();
                if (project.SortLots && numbered.Count > 0) { Lots.MoveToRow(page, table, numbered, before, r); break; }
                var order = Lots.RowOrder(table).Where(x => x != r).ToList();
                order.Insert(before is int b ? order.IndexOf(b) : order.Count, r);
                table.RowOrder = order;
                break;
            }
            case "dragUnit":
            {
                // Poignée ⋮⋮ d'une ligne rangée ou déplacée (MainWindow.UnitMoved) : posée dans le tableau où elle s'affiche.
                int? before = a.GetProperty("before").ValueKind == JsonValueKind.Null ? null : a.GetProperty("before").GetInt32();
                var u = Arrangement.Build(project).Moving.FirstOrDefault(x => (x.Kind == "sort" || x.Kind == "move") && x.Lines.Contains(line));
                if (u != null) Lots.MoveToRow(u.Dest.Page, u.Dest.Table, u.Lines.Where(l => Lots.HasCell(l) && !Lots.IsZero(l)).ToList(), before);
                break;
            }
            case "number": Lots.Number(project, 1); break;
            case "trim": Rows.CompactRow(page, line); break;
            case "gap":
            {
                var gs = Rows.RowGaps(page, line);
                int k = a.GetProperty("k").GetInt32();
                if (k < gs.Count) Rows.SetGap(page, gs[k].Line, gs[k].Key, a.GetProperty("want").GetInt32());
                break;
            }
            case "lotPos": line.LotPos = a.GetProperty("v").ValueKind == JsonValueKind.Null ? null : a.GetProperty("v").GetInt32(); break;
            case "untrim": Rows.UntrimRow(page, line); break;
            case "moveRow":
            {
                var order = Lots.RowOrder(table);
                int from = order[o];
                var rest = order.Where(x => x != from).ToList();
                rest.Insert(Math.Min(a.GetProperty("to").GetInt32(), rest.Count), from);
                table.RowOrder = rest;
                break;
            }
            case "moveLine":
            {
                var at = Rows.Locate(page, line)!;
                var order0 = Lots.RowOrder(table);
                int to0 = a.GetProperty("to").GetInt32();
                var target = to0 < order0.Count ? table.Rows[order0[to0]] : null;
                var prev = at.I > 0 ? at.Row.Lines[at.I - 1] : null;
                if (prev != null) Rows.Split(page, line);
                var own = Rows.Locate(page, line)!;
                var next = own.I + 1 < own.Row.Lines.Count ? own.Row.Lines[own.I + 1] : null;
                if (next != null) Rows.SplitBelow(page, line);
                foreach (var l in new[] { prev, line, next }) if (l != null) Rows.TrimRow(page, l);
                int from = table.Rows.FindIndex(x => x.Lines.Contains(line));
                int to = target is null ? -1 : table.Rows.IndexOf(target);
                if (to != from)
                {
                    var order = Lots.RowOrder(table).Where(x => x != from).ToList();
                    order.Insert(to < 0 ? order.Count : order.IndexOf(to), from);
                    table.RowOrder = order;
                }
                break;
            }
            case "placeZero":
            {
                var order = Lots.RowOrder(table);
                var anchorRow = table.Rows[order[Math.Min(a.GetProperty("to").GetInt32(), order.Count - 1)]];
                var anchor = anchorRow.Lines.FirstOrDefault(l => !string.IsNullOrEmpty(l.Cells.FirstOrDefault()?.Id));
                if (anchor != null) line.EndAt = new EndAnchor { Page = page.Id, Line = anchor.Cells[0].Id };
                break;
            }
            case "total":
            {
                Rows.TrimRow(page, line);
                string mode = a.GetProperty("mode").GetString()!;
                Rows.AddTotalLine(page, line, new TotalScope { Mode = mode, Pages = mode == "pages" ? [pages[0].Id] : [], Auto = true, Bold = a.GetProperty("bold").GetBoolean() });
                break;
            }
            case "endMode":
                project.EndLots = a.GetProperty("page").GetBoolean()
                    ? new EndLotsChoice { Mode = "page", AfterId = pages[a.GetProperty("after").GetInt32()].Id }
                    : new EndLotsChoice { Mode = "last", AfterId = null };
                break;
            default: throw new InvalidOperationException("manipulation inconnue " + op);
        }
    }

    private static void Check(InventoryProject project, Arrangement.Plan plan, Dictionary<string, int> baseH, int step, string op, Action<int, string> fail)
    {
        foreach (var page in project.Pages)
        {
            if (page.EndLots) continue;
            if (PageLayout.BaseHeight(page) != baseH[page.Id]) fail(step, $"{op} : page {page.Id} hauteur d'origine {PageLayout.BaseHeight(page)} ≠ {baseH[page.Id]}");
            for (int ti = 0; ti < page.Tables.Count; ti++)
            {
                var t = page.Tables[ti];
                if (!t.HasGrid || t.RowYs.Count < 2 || t.ColumnXs.Count < 2) continue;
                for (int k = 1; k < t.RowYs.Count; k++) if (!(t.RowYs[k] > t.RowYs[k - 1])) fail(step, $"{op} : filets dans le désordre");
                if (t.Rows.Count + 1 != t.RowYs.Count) fail(step, $"{op} : {t.Rows.Count} rangées pour {t.RowYs.Count} filets");
                for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
                    foreach (var l in t.Rows[r].Lines)
                        foreach (var f in l.Cells)
                        {
                            double cy = f.Box.Y + (f.Box.Height - l.Extra) / 2;
                            if (!(cy > t.RowYs[r] - 1 && cy < t.RowYs[r + 1] + 1)) fail(step, $"{op} : « {l.FirstText} » hors de sa rangée");
                        }
            }
        }
        // Contrôles de plus : morceaux d'image cohérents, rangées jamais écrasées, texte agrandi qui ne recouvre rien, cases uniques.
        var seen = new HashSet<string>();
        foreach (var page in project.Pages)
        {
            foreach (var f in page.AllFields()) if (f.Id.Length > 0 && !seen.Add(f.Id)) fail(step, $"{op} : case {f.Id} en double");
            if (page.EndLots) continue;
            var seq = PageLayout.Pieces(page);
            if (seq.Sum(x => x.N) != page.Height) fail(step, $"{op} : page {page.Id} morceaux {seq.Sum(x => x.N)} ≠ hauteur {page.Height}");
            foreach (var x in seq) if (x.N <= 0 || x.Y0 < 0 || (!x.Extra && !x.Gap && x.Y0 + x.N > baseH[page.Id])) fail(step, $"{op} : page {page.Id} morceau hors de l'image ({x.Y0}+{x.N})");
            foreach (var t in page.Tables)
            {
                if (!t.HasGrid || t.RowYs.Count < 2 || t.ColumnXs.Count < 2) continue;
                for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
                {
                    if (t.RowYs[r + 1] - t.RowYs[r] < 4) fail(step, $"{op} : page {page.Id} rangée écrasée ({t.RowYs[r]}–{t.RowYs[r + 1]})");
                    var lines = t.Rows[r].Lines;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var l = lines[i];
                        if (l.Extra <= 0) continue;
                        if (t.RowYs[r + 1] < l.ExtraAt + l.Extra - 1) fail(step, $"{op} : « {l.FirstText} » agrandie hors de sa rangée");
                    }
                }
            }
        }
        foreach (var page in project.Pages.Where(pg => pg.Added != null))
        {
            if (page.Added!.Tables.Count != page.Tables.Count) fail(step, $"{op} : page ajoutée repères {page.Added.Tables.Count} ≠ {page.Tables.Count}");
            for (int ti = 0; ti < page.Tables.Count; ti++)
            {
                if (ti >= page.Added.Tables.Count || page.Added.Tables[ti] is null) continue;
                var t = page.Tables[ti];
                int n = t.ColumnXs.Count - 1;
                foreach (var l in t.Rows.SelectMany(r => r.Lines))
                {
                    var cols = l.Cells.Select(f => f.Column).ToList();
                    if (cols.Distinct().Count() != cols.Count || cols.Any(c => c < 0 || c >= n)) fail(step, $"{op} : page ajoutée cases {string.Join(",", cols)} pour {n} colonnes");
                    foreach (var f in l.Cells.Where(f => f.Column >= 0 && f.Column < n))
                        if (!(f.Box.X >= t.ColumnXs[f.Column] - 2 && f.Box.Right <= t.ColumnXs[f.Column + 1] + 2)) fail(step, $"{op} : page ajoutée case hors de sa colonne");
                }
            }
        }
        foreach (var o in Lots.ObjectLines(project)) if (!Lots.HasCell(o.Line)) fail(step, $"{op} : objet « {o.Line.FirstText} » sans case de lot");
        // Lignes rangées par numéro : à l'écran, de page en page, les numéros ne redescendent jamais
        // (sauf les lignes posées à la main, les lots 0, les cases vides et les lignes encore vides).
        if (project.SortLots)
        {
            var objSet = Lots.ObjectLines(project).Select(x => x.Line).ToHashSet();
            PrintedLine? prev = null;
            for (int k = 0; k < project.Pages.Count; k++)
                foreach (var e in Arrangement.ViewOf(project, plan, k).Lines
                    .Where(e => e.LotBox != null && objSet.Contains(e.Line) && e.Line.MoveTo == null && !e.Line.Deleted && e.Line.Lot.Trim().Length > 0 && !Lots.IsZero(e.Line) && e.Line.Cells.Any(c => c.Text.Trim().Length > 0))
                    .OrderBy(e => e.LotBox!.Value.Y))
                {
                    if (prev != null && Arrangement.CompareKeys(Arrangement.LotKey(prev.Lot), Arrangement.LotKey(e.Line.Lot)) > 0)
                        fail(step, $"{op} : tri cassé, « {prev.Lot} » avant « {e.Line.Lot} » page {k + 1}");
                    prev = e.Line;
                }
        }
        for (int k = 0; k < project.Pages.Count; k++)
        {
            var v = Arrangement.ViewOf(project, plan, k);
            if (Lots.IsActive(project))
                foreach (var e in v.Lines) if (!e.CarriedOnly && Lots.HasCell(e.Line) && !e.Table.LotsOff && e.LotBox is null) fail(step, $"{op} : « {e.Line.FirstText} » sans case à l'écran");
        }
    }
}
