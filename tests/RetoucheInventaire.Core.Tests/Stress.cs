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
        Console.WriteLine($"Windows : {step} manipulations rejouées, {project.Pages.Count} pages, {Lots.ObjectLines(project).Count()} objets.");
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
