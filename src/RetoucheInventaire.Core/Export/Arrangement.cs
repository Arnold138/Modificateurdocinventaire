using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Export;

/// <summary>
/// Mise en page des rangées déplacées : lots 0 envoyés après le dernier lot, rangées montées/descendues à la main.
/// Rien n'est modifié dans le projet : la page affichée (et exportée) est recomposée à partir des pages d'origine.
/// <list type="bullet">
/// <item>Lot 0 : l'objet (avec ses lignes de suite et, s'il les reprend, les valeurs de son titre) quitte sa rangée,
/// ce qui suit remonte ; il est recollé juste après la rangée du dernier objet numéroté (même sur une autre page),
/// dans une rangée refaite aux colonnes du tableau d'arrivée ; ce qui suit descend.</item>
/// <item>Rangée déplacée à la main : l'ordre des rangées du tableau change (<see cref="Table.RowOrder"/>).</item>
/// </list>
/// Calculs en entiers : résultat identique, pixel pour pixel, à la version iPhone/iPad (arrange.js).
/// </summary>
public static class Arrangement
{
    /// <summary>Une rangée commence 2 px au-dessus du milieu de son filet du haut.</summary>
    public const int Cut = 2;
    /// <summary>Hauteur réservée au filet du haut.</summary>
    public const int Filet = 5;
    /// <summary>Marge entre un filet vertical et le contenu recopié d'une case.</summary>
    public const int Margin = 3;

    /// <summary>Rangée de la destination : là où les lots 0 sont recollés.</summary>
    public sealed record Destination(int P, int Ti, int R, Table Table, Page Page, bool Before = false);

    /// <summary>Un objet au lot 0 et le morceau de page qu'il emporte.</summary>
    public sealed class Unit
    {
        public int P { get; init; }
        public Page Page { get; init; } = null!;
        public int Ti { get; init; }
        public Table Table { get; init; } = null!;
        public int R { get; init; }
        public PrintedLine Object { get; init; } = null!;
        public List<PrintedLine> Lines { get; init; } = [];
        /// <summary>Titre de la même rangée dont les valeurs partent avec l'objet.</summary>
        public PrintedLine? Title { get; init; }
        public bool Whole { get; init; }
        public int Top { get; init; }
        public int BodyTop { get; init; }
        public int Bottom { get; init; }
        /// <summary>« zero » (lot 0 : recollé à la fin) ou « deleted » (ligne supprimée : retirée).</summary>
        public string Kind { get; init; } = "zero";
        /// <summary>Page « lots de fin » où il est recollé (-1 : ailleurs).</summary>
        public int EndPage { get; set; } = -1;
        public Destination Dest { get; set; } = null!;
        public int[] Shift { get; set; } = [];
        public int LotShift { get; set; }
        public int SrcLotWidth { get; set; }
        public int DstLotWidth { get; set; }
        public int Height { get; set; }
    }

    /// <summary>Morceau d'une page : recopie des lignes [Y0, Y1[ de la page d'origine, rangée refaite d'un lot 0, ou papier (H lignes).</summary>
    public sealed class Piece
    {
        public bool IsUnit => Unit != null;
        public bool Paper { get; init; }
        public bool IsCopy => Unit is null && !Paper;
        public int Y0 { get; set; }
        public int Y1 { get; set; }
        public int Dst { get; set; }
        public Unit? Unit { get; init; }
        public int H { get; init; }
    }

    /// <summary>Plan d'une page ; <paramref name="Template"/> : page modèle d'une page « lots de fin » (-1 : aucune), null sinon.</summary>
    public sealed record PagePlan(int Height, List<Piece> Pieces, int? Template = null);
    public sealed record Erase(int P, int X0, int Y0, int X1, int Y1);
    /// <summary>Units : tout ce qui quitte sa place (lots 0 recollés et lignes supprimées) ; Moving : les lots 0 recollés.</summary>
    public sealed record Plan(List<PagePlan> Pages, List<Unit> Units, List<Unit> Moving, Destination? Dest, List<Erase> Erase, int EndNeeded, InventoryProject? Project = null);

    private static int R(double v) => (int)Math.Floor(v + 0.5);
    internal static int CutY(double y) => R(y) - Cut;
    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
    private static bool HasValues(PrintedLine l) => l.Cells.Skip(1).Any(c => !Blank(c.Text));
    private static bool GridTable(Table t) => t.HasGrid && t.RowYs.Count >= 2 && t.ColumnXs.Count >= 2;

    internal static double LineTop(PrintedLine l)
    {
        var ink = l.Cells.Where(c => !c.Ink.IsEmpty).ToList();
        return ink.Count > 0 ? ink.Min(c => c.Ink.Top) : l.Cells.Min(c => PageLayout.BaseBox(l, c).Top);
    }

    internal static double LineBottom(PrintedLine l)
    {
        var ink = l.Cells.Where(c => !c.Ink.IsEmpty).ToList();
        double b = ink.Count > 0 ? ink.Max(c => c.Ink.Bottom) : l.Cells.Max(c => PageLayout.BaseBox(l, c).Bottom);
        if (l.Extra > 0) b = Math.Max(b, l.ExtraAt + l.Extra);
        return b;
    }

    internal static int GapMid(PrintedLine a, PrintedLine b) => (int)Math.Floor((LineBottom(a) + LineTop(b)) / 2);

    /// <summary>Titre de la rangée dont les valeurs descendent sur <paramref name="target"/> (comme dans le .txt).</summary>
    private static PrintedLine? CarriedTitle(List<PrintedLine> lines, PrintedLine target)
    {
        var taken = new HashSet<PrintedLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != LineKind.Titre || !HasValues(lines[i])) continue;
            var t = lines.Skip(i + 1).FirstOrDefault(x => x.Kind != LineKind.Titre && !HasValues(x) && !taken.Contains(x));
            if (t is null) continue;
            taken.Add(t);
            if (t == target) return lines[i];
        }
        return null;
    }

    /// <summary>
    /// Lignes qui quittent leur place, dans l'ordre du document : « zero » (objet au lot 0 et sa suite, recollé après le dernier lot
    /// ou sur une page « lots de fin ») ; « deleted » (ligne supprimée et sa suite : retirée, la suite remonte).
    /// </summary>
    /// <summary>
    /// Clé de tri d'un numéro de lot : les nombres du plus petit au plus grand (les « 1 » ensemble, puis les « 2 »…), puis
    /// les codes en lettres groupés (« ? », « VGG »…), puis les cases vides.
    /// </summary>
    /// <summary>
    /// Tri par numéro de lot, en bougeant le moins de lignes possible : la plus longue suite déjà dans l'ordre reste en
    /// place ; chaque autre objet est recollé juste après la rangée du dernier objet resté en place qui a un numéro plus petit
    /// ou égal (avant le 1er : après la rangée qui précède). Renvoie les objets à déplacer, dans l'ordre des numéros.
    /// </summary>
    /// <summary>
    /// Où va un lot déplacé à la main (PrintedLine.MoveTo) : au début de la page (avant sa 1re rangée d'objet), à la fin
    /// (après sa dernière rangée d'objet) ou juste après la rangée d'un lot choisi. null si l'endroit n'existe plus.
    /// </summary>
    private static Destination? MoveDest(InventoryProject project, Unit u, List<OrderedRow> rows)
    {
        var m = u.Object.MoveTo!;
        int pt = project.Pages.FindIndex(pg => pg.Id == m.Page);
        if (pt < 0) return null;
        var here = rows.Where(x => x.P == pt).ToList();
        if (here.Count == 0) return null;
        var objRows = here.Where(x => x.Table.Rows[x.R].Lines.Any(l => Lots.HasCell(l) && !l.Deleted)).ToList();
        Destination At(OrderedRow x, bool before = false) => new(x.P, x.Ti, x.R, x.Table, x.Page, before);
        if (m.Where == "after")
        {
            var x = here.FirstOrDefault(y => y.Table.Rows[y.R].Lines.Any(l => l.Cells.Count > 0 && l.Cells[0].Id == m.Anchor));
            if (x != null) return At(x);
        }
        if (m.Where == "order")
        {
            // À sa place dans l'ordre des numéros de cette page : après la dernière rangée qui a un numéro plus petit ou égal.
            var k = LotKey(u.Object.Lot);
            OrderedRow? last = null;
            foreach (var x in objRows)
                if (x.Table.Rows[x.R].Lines.Any(l => l != u.Object && Lots.HasCell(l) && l.MoveTo == null && l.Lot.Trim().Length > 0 && CompareKeys(LotKey(l.Lot), k) <= 0)) last = x;
            if (last != null) return At(last);
            return objRows.Count > 0 ? At(objRows[0], true) : At(here[0], true);
        }
        if (m.Where == "start") return objRows.Count > 0 ? At(objRows[0], true) : At(here[0], true);
        return objRows.Count > 0 ? At(objRows[^1]) : At(here[^1]);
    }

    private static List<(Unit U, Destination D)> SortPlaces(List<Unit> units, List<OrderedRow> rows)
    {
        var keys = units.Select(u => LotKey(u.Object.Lot)).ToList();
        var tails = new List<int>();
        var prev = Enumerable.Repeat(-1, units.Count).ToArray();
        for (int i = 0; i < units.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (CompareKeys(keys[tails[mid]], keys[i]) <= 0) lo = mid + 1; else hi = mid; }
            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var stay = new HashSet<int>();
        for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = prev[i]) stay.Add(i);
        int RowIndex(Unit u) => rows.FindIndex(x => x.P == u.P && x.Ti == u.Ti && x.R == u.R);
        // Avant le 1er objet resté en place : la rangée qui le précède ; s'il n'y en a pas (1re rangée du document), il bouge aussi.
        int first = -1;
        for (int i = 0; i < units.Count; i++)
        {
            if (!stay.Contains(i)) continue;
            if (RowIndex(units[i]) > 0) { first = i; break; }
            stay.Remove(i);
        }
        var order = Enumerable.Range(0, units.Count).ToList();
        order.Sort((a, b) => { int c = CompareKeys(keys[a], keys[b]); return c != 0 ? c : a.CompareTo(b); });
        var output = new List<(Unit, Destination)>();
        foreach (int i in order)
        {
            if (stay.Contains(i)) continue;
            int anchor = -1;
            foreach (int j in stay) if (CompareKeys(keys[j], keys[i]) <= 0 && j > anchor) anchor = j;
            Destination d;
            if (anchor >= 0) { var a = units[anchor]; d = new Destination(a.P, a.Ti, a.R, a.Table, a.Page); }
            else if (first >= 0) { var x = rows[RowIndex(units[first]) - 1]; d = new Destination(x.P, x.Ti, x.R, x.Table, x.Page); }
            else continue; // aucun repère : la ligne reste où elle est
            output.Add((units[i], d));
        }
        return output;
    }

    public static (int, int, string) LotKey(string? lot)
    {
        string t = (lot ?? "").Trim();
        if (t.Length == 0) return (2, 0, "");
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return (0, n, t[m.Groups[1].Value.Length..].ToUpperInvariant());
        return (1, 0, t.ToUpperInvariant());
    }

    public static int CompareKeys((int, int, string) a, (int, int, string) b)
    {
        int c = a.Item1.CompareTo(b.Item1);
        if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2);
        return c != 0 ? c : string.CompareOrdinal(a.Item3, b.Item3);
    }

    private static List<Unit> MovedUnits(InventoryProject project)
    {
        var units = new List<Unit>();
        bool lotsOn = Lots.IsActive(project);
        // Tri par numéro de lot (Lots › Trier) : chaque objet numéroté peut partir à la place de son numéro.
        bool sorting = lotsOn && project.SortLots;
        var objects = sorting ? Lots.ObjectLines(project).Select(o => o.Line).ToHashSet() : null;
        for (int p = 0; p < project.Pages.Count; p++)
        {
            var page = project.Pages[p];
            for (int ti = 0; ti < page.Tables.Count; ti++)
            {
                var table = page.Tables[ti];
                if (!GridTable(table)) continue;
                foreach (var r in Lots.RowOrder(table))
                {
                    if (r + 1 >= table.RowYs.Count) continue;
                    var row = table.Rows[r];
                    var lines = row.Lines.Where(l => l.Kind != LineKind.Ignoree && (l.Cells.Any(c => !Blank(c.Text)) || l.Deleted)).ToList();
                    var found = new List<(string Kind, PrintedLine Object, List<PrintedLine> Lines, PrintedLine? Title)>();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var l = lines[i];
                        bool deleted = l.Deleted;
                        bool zero = lotsOn && Lots.HasCell(l) && Lots.IsZero(l);
                        bool move = !deleted && !zero && lotsOn && Lots.HasCell(l) && l.MoveTo != null;
                        // Case encore vide : elle reste à sa place (elle n'a pas de numéro à suivre).
                        bool sort = !deleted && !zero && !move && sorting && objects!.Contains(l) && l.Lot.Trim().Length > 0;
                        if (!deleted && !zero && !move && !sort) continue;
                        if (found.Any(u => u.Lines.Contains(l))) continue;
                        var own = new List<PrintedLine> { l };
                        for (int j = i + 1; j < lines.Count && lines[j].Kind != LineKind.Titre && !Lots.HasCell(lines[j]) && !lines[j].Deleted; j++)
                        {
                            // Ligne supprimée : sa suite (texte sans montant), pas un autre objet (« 1 coffre fort… »).
                            if (deleted && (HasValues(lines[j]) || Analysis.LineClassifier.LooksLikeItem(lines[j].Cells.Count > 0 ? lines[j].Cells[0].Text : ""))) break;
                            own.Add(lines[j]);
                        }
                        found.Add((deleted ? "deleted" : zero ? "zero" : move ? "move" : "sort", l, own, HasValues(l) ? null : CarriedTitle(lines, l)));
                    }
                    if (found.Count == 0) continue;
                    var covered = found.SelectMany(u => u.Lines).ToHashSet();
                    bool whole = lines.All(covered.Contains) && found.All(u => u.Title is null) && found.All(u => u.Kind == found[0].Kind);
                    if (whole)
                    {
                        int top = CutY(table.RowYs[r]);
                        units.Add(new Unit
                        {
                            P = p, Page = page, Ti = ti, Table = table, R = r, Kind = found[0].Kind, Object = found[0].Object, Lines = [.. row.Lines],
                            Whole = true, Top = top, BodyTop = top + Filet, Bottom = CutY(table.RowYs[r + 1]),
                        });
                        continue;
                    }
                    foreach (var u in found)
                    {
                        int first = lines.IndexOf(u.Lines[0]), last = lines.IndexOf(u.Lines[^1]);
                        int top = first > 0 ? GapMid(lines[first - 1], lines[first]) : CutY(table.RowYs[r]) + Filet;
                        int bottom = last + 1 < lines.Count ? GapMid(lines[last], lines[last + 1]) : CutY(table.RowYs[r + 1]);
                        if (bottom <= top) continue;
                        units.Add(new Unit
                        {
                            P = p, Page = page, Ti = ti, Table = table, R = r, Kind = u.Kind, Object = u.Object, Lines = u.Lines, Title = u.Title,
                            Whole = false, Top = top, BodyTop = top, Bottom = bottom,
                        });
                    }
                }
            }
        }
        return units;
    }

    /// <summary>Lignes supprimées (et leur suite) : hors du .txt, des totaux et des lots.</summary>
    public static HashSet<PrintedLine> DeletedLines(InventoryProject project) =>
        MovedUnits(project).Where(u => u.Kind == "deleted").SelectMany(u => u.Lines).ToHashSet();

    /// <summary>Page modèle des pages « lots de fin » : la dernière page à lots avant elles (sinon celle du dernier lot).</summary>
    private static Destination? EndTemplate(InventoryProject project)
    {
        int first = project.Pages.FindIndex(pg => pg.EndLots);
        var objects = Lots.ObjectLines(project).ToList();
        (Page Page, Table Table, int Row, PrintedLine Line)? best = null;
        foreach (var o in objects)
        {
            int p = project.Pages.IndexOf(o.Page);
            if (!GridTable(o.Table) || (first >= 0 && p > first)) continue;
            best = o;
        }
        if (best is null)
        {
            var grid = objects.Where(o => GridTable(o.Table)).ToList();
            if (grid.Count > 0) best = grid[^1];
        }
        if (best is null) return null;
        var b = best.Value;
        // Rangée de référence (filet, fond, filets verticaux) : la 1re rangée d'objet du tableau.
        int r = objects.FirstOrDefault(o => o.Table == b.Table).Line != null ? objects.First(o => o.Table == b.Table).Row : b.Row;
        return new Destination(project.Pages.IndexOf(b.Page), b.Page.Tables.IndexOf(b.Table), r, b.Table, b.Page);
    }

    /// <summary>
    /// Mode page dédiée : crée ou retire les pages « lots de fin » pour qu'il y en ait juste assez (une de plus quand la précédente est
    /// pleine, aucune sans lot 0). La 1re est placée après la page choisie (<see cref="EndLotsChoice.AfterId"/>). Vrai si les pages ont changé.
    /// </summary>
    public static bool SyncEndPages(InventoryProject project)
    {
        bool changed = false;
        for (int guard = 0; guard < 50; guard++)
        {
            var plan = Build(project);
            var ends = project.Pages.Where(pg => pg.EndLots).ToList();
            int need = project.EndLots?.Mode == "page" ? plan.EndNeeded : 0;
            if (ends.Count == need)
            {
                // Même taille que la page modèle.
                if (EndTemplate(project) is { } t)
                    foreach (var pg in ends.Where(pg => pg.Width != t.Page.Width || pg.Height != t.Page.Height)) { pg.Width = t.Page.Width; pg.Height = t.Page.Height; changed = true; }
                return changed;
            }
            changed = true;
            if (ends.Count > need) { project.Pages.RemoveAt(project.Pages.LastIndexOf(ends[^1])); continue; }
            int at;
            if (ends.Count > 0) at = project.Pages.LastIndexOf(ends[^1]) + 1;
            else
            {
                int after = project.Pages.FindIndex(pg => pg.Id == project.EndLots!.AfterId);
                at = after >= 0 ? after + 1 : project.Pages.Count;
            }
            var model = ends.Count > 0 ? ends[0] : project.Pages.FirstOrDefault(pg => !pg.EndLots && pg.Tables.Count > 0) ?? project.Pages.FirstOrDefault();
            project.Pages.Insert(at, new Page
            {
                Id = "fin-" + Guid.NewGuid().ToString("N")[..8], EndLots = true, Source = PageSource.Image,
                SourceName = ends.Count == 0 ? "Lots de fin" : $"Lots de fin {ends.Count + 1}",
                Width = model?.Width ?? 1000, Height = model?.Height ?? 1400,
            });
        }
        return changed;
    }

    /// <summary>Rangée du dernier objet numéroté (pas au lot 0) : c'est là que les lots 0 sont recollés.</summary>
    private static Destination? FindDestination(InventoryProject project)
    {
        (Page Page, Table Table, int Row, PrintedLine Line)? last = null;
        foreach (var o in Lots.ObjectLines(project)) if (!Lots.IsZero(o.Line)) last = o;
        if (last is null || !GridTable(last.Value.Table)) return null;
        var (page, table, row, _) = last.Value;
        return new Destination(project.Pages.IndexOf(page), page.Tables.IndexOf(table), row, table, page);
    }

    private sealed record OrderedRow(int P, int Ti, int R, Table Table, Page Page);

    /// <summary>Rangées des tableaux à filets, dans l'ordre affiché, toutes pages (pas les pages « lots de fin »).</summary>
    private static List<OrderedRow> RowsInOrder(InventoryProject project)
    {
        var output = new List<OrderedRow>();
        for (int p = 0; p < project.Pages.Count; p++)
        {
            var page = project.Pages[p];
            if (page.EndLots) continue;
            foreach (var (t, ti) in page.Tables.Select((t, ti) => (t, ti)).Where(x => GridTable(x.t)).OrderBy(x => x.t.RowYs[0]))
                foreach (var r in Lots.RowOrder(t))
                    if (r + 1 < t.RowYs.Count) output.Add(new OrderedRow(p, ti, r, t, page));
        }
        return output;
    }

    private static Destination DestOf(OrderedRow x) => new(x.P, x.Ti, x.R, x.Table, x.Page);

    /// <summary>
    /// Où recoller ce lot 0 : l'endroit choisi à la main (poignée ⋮⋮ en mode Modifier, <see cref="PrintedLine.EndAt"/> : après la
    /// rangée de cette ligne) ; sinon à la fin de sa partie : après le dernier objet numéroté avant la ligne TOTAL qui la ferme
    /// (« TOTAL MOBILIER ET MATERIEL ») ; sans ligne TOTAL après lui, après le dernier lot du document.
    /// </summary>
    private static Destination UnitDestination(Unit u, List<OrderedRow> rows, HashSet<PrintedLine> numbered, Destination fallback)
    {
        if (u.Object.EndAt is { } at)
        {
            var x = rows.FirstOrDefault(x => x.Page.Id == at.Page && x.Table.Rows[x.R].Lines.Any(l => l.Cells.FirstOrDefault()?.Id == at.Line));
            if (x != null) return DestOf(x);
        }
        int k = rows.FindIndex(x => x.P == u.P && x.Ti == u.Ti && x.R == u.R);
        OrderedRow? last = null;
        for (int i = Math.Max(0, k); i < rows.Count; i++)
        {
            var lines = rows[i].Table.Rows[rows[i].R].Lines;
            if (lines.Any(l => !l.Deleted && Analysis.LineClassifier.LooksLikeTotal(l))) return DestOf(last ?? rows[Math.Max(k, i - 1)]);
            if (i > k && lines.Any(numbered.Contains)) last = rows[i];
        }
        return fallback;
    }

    /// <summary>Décalage horizontal d'une colonne vers le tableau d'arrivée (selon l'alignement de la colonne).</summary>
    private static int ColumnShift(Table src, Table dst, int c, TextAlign align)
    {
        var a = src.ColumnXs; var b = dst.ColumnXs;
        if (a.Count != b.Count) return R(b[0] - a[0]);
        return align switch
        {
            TextAlign.Right => R(b[c + 1] - a[c + 1]),
            TextAlign.Center => R((b[c] + b[c + 1] - a[c] - a[c + 1]) / 2),
            _ => R(b[c] - a[c]),
        };
    }

    /// <summary>
    /// Plan de toutes les pages : morceaux recopiés (dans l'ordre), rangées des lots 0, papier. Pages « lots de fin » : composées depuis
    /// la page modèle (marge, lots 0 en rangées du tableau modèle, filet du bas). EndNeeded : pages « lots de fin » nécessaires.
    /// </summary>
    public static Plan Build(InventoryProject project)
    {
        var all = MovedUnits(project);
        var zeros = all.Where(u => u.Kind == "zero").ToList();
        var deleted = all.Where(u => u.Kind == "deleted").ToList();
        var slots = all.Where(u => u.Kind == "sort").ToList();
        bool endMode = project.EndLots?.Mode == "page";
        var template = endMode && zeros.Count > 0 ? EndTemplate(project) : null;
        var endIdx = project.Pages.Select((pg, k) => pg.EndLots ? k : -1).Where(k => k >= 0).ToList();
        Destination? dest = null;
        int endNeeded = 0;
        var moving = new List<Unit>();
        bool lots = Lots.IsActive(project);
        void Prepare(Unit u, Destination d)
        {
            TextAlign Align(int c) => c < u.Object.Cells.Count ? u.Object.Cells[c].Align : (c == 0 ? TextAlign.Left : TextAlign.Right);
            u.Dest = d;
            u.Shift = Enumerable.Range(0, u.Table.ColumnXs.Count - 1).Select(c => ColumnShift(u.Table, d.Table, c, Align(c))).ToArray();
            u.LotShift = R(Lots.TableLeft(d.Table)) - R(Lots.TableLeft(u.Table));
            u.SrcLotWidth = lots ? R(Lots.ColumnWidth(u.Page)) : 0;
            u.DstLotWidth = lots ? R(Lots.ColumnWidth(d.Page)) : 0;
            u.Height = Filet + (u.Bottom - u.BodyTop);
        }
        if (template != null)
        {
            // Pages dédiées : les lots 0 à la suite, une nouvelle page quand la hauteur du tableau modèle est atteinte.
            var T = template.Table;
            int avail = CutY(T.RowYs[^1]) - CutY(T.RowYs[0]), used = 0;
            var groups = new List<List<Unit>>();
            foreach (var u in zeros)
            {
                int h = Filet + (u.Bottom - u.BodyTop);
                if (groups.Count == 0 || (used + h > avail && used > 0)) { groups.Add([]); used = 0; }
                groups[^1].Add(u);
                used += h;
            }
            endNeeded = groups.Count;
            for (int i = 0; i < groups.Count && i < endIdx.Count; i++) // page pas encore créée : ces lots restent à leur place
            {
                foreach (var u in groups[i]) { Prepare(u, template with { P = endIdx[i] }); u.EndPage = endIdx[i]; moving.Add(u); }
                if (i == 0) dest = new Destination(endIdx[0], -1, -1, template.Table, template.Page);
            }
        }
        else if (zeros.Count > 0)
        {
            if (FindDestination(project) is { } fallback)
            {
                var rows = RowsInOrder(project);
                var zeroLines = zeros.SelectMany(u => u.Lines).ToHashSet();
                var numbered = Lots.ObjectLines(project).Select(o => o.Line).Where(l => !Lots.IsZero(l) && !l.Deleted && !zeroLines.Contains(l)).ToHashSet();
                moving = zeros;
                foreach (var u in moving) Prepare(u, UnitDestination(u, rows, numbered, fallback));
                dest = moving[0].Dest;
            }
        }
        var sortMoves = new List<Unit>();
        var ordered = RowsInOrder(project);
        if (slots.Count > 0)
            foreach (var (u, d) in SortPlaces(slots, ordered)) { Prepare(u, d); sortMoves.Add(u); }
        // Lots déplacés à la main (Lots › Déplacer des lots) : vers la page et l'endroit choisis.
        var handMoves = new List<Unit>();
        foreach (var u in all.Where(x => x.Kind == "move")) if (MoveDest(project, u, ordered) is { } md) { Prepare(u, md); handMoves.Add(u); }
        moving = sortMoves.Concat(handMoves).Concat(moving).ToList();
        var cutting = moving.Concat(deleted).ToList();

        var pages = new List<PagePlan>();
        for (int p = 0; p < project.Pages.Count; p++)
        {
            var page = project.Pages[p];
            var pieces = new List<Piece>();
            int dst = 0;
            void Copy(int y0, int y1)
            {
                if (y1 <= y0) return;
                var last = pieces.LastOrDefault();
                if (last != null && last.IsCopy && last.Y1 == y0) { last.Y1 = y1; dst += y1 - y0; return; }
                pieces.Add(new Piece { Y0 = y0, Y1 = y1, Dst = dst });
                dst += y1 - y0;
            }
            if (page.EndLots)
            {
                // Page « lots de fin » : marge du haut (papier), lots 0 en rangées du tableau modèle, filet du bas, papier.
                if (template is null) { pages.Add(new PagePlan(Math.Max(1, page.Height), [new Piece { Paper = true, Dst = 0, H = Math.Max(1, page.Height) }], -1)); continue; }
                var T = template.Table;
                int top = CutY(T.RowYs[0]), bottom = CutY(T.RowYs[^1]);
                pieces.Add(new Piece { Paper = true, Dst = 0, H = top });
                dst = top;
                foreach (var u in moving.Where(u => u.EndPage == p)) { pieces.Add(new Piece { Unit = u, Dst = dst, H = u.Height }); dst += u.Height; }
                Copy(bottom, bottom + Filet);
                int H = Math.Max(template.Page.Height, dst);
                if (H > dst) { pieces.Add(new Piece { Paper = true, Dst = dst, H = H - dst }); dst = H; }
                pages.Add(new PagePlan(dst, pieces, template.P));
                continue;
            }
            int cursor = 0;
            var tables = page.Tables.Select((t, ti) => (T: t, Ti: ti)).Where(x => GridTable(x.T)).OrderBy(x => x.T.RowYs[0]).ToList();
            foreach (var (t, ti) in tables)
            {
                int n = t.RowYs.Count - 1;
                int A = Math.Max(cursor, CutY(t.RowYs[0])), B = CutY(t.RowYs[n]);
                if (B <= A) continue;
                Copy(cursor, A);
                foreach (var r in Lots.RowOrder(t))
                {
                    if (r >= n) continue;
                    int s0 = Math.Max(A, CutY(t.RowYs[r])), s1 = Math.Min(B, CutY(t.RowYs[r + 1]));
                    var cuts = cutting.Where(u => u.P == p && u.Ti == ti && u.R == r).OrderBy(u => u.Top).ToList();
                    if (template is null)
                        foreach (var u in moving.Where(u => u.Dest.P == p && u.Dest.Ti == ti && u.Dest.R == r && u.Dest.Before)) { pieces.Add(new Piece { Unit = u, Dst = dst, H = u.Height }); dst += u.Height; }
                    int y = s0;
                    foreach (var u in cuts) { Copy(y, Math.Max(y, u.Top)); y = Math.Max(y, u.Bottom); }
                    Copy(y, s1);
                    if (template is null)
                        foreach (var u in moving.Where(u => u.Dest.P == p && u.Dest.Ti == ti && u.Dest.R == r && !u.Dest.Before)) { pieces.Add(new Piece { Unit = u, Dst = dst, H = u.Height }); dst += u.Height; }
                }
                cursor = B;
            }
            Copy(cursor, page.Height);
            pages.Add(new PagePlan(Math.Max(1, dst), pieces));
        }
        // Valeurs d'un titre parties avec son objet (ou supprimées avec lui) : effacées de leur place.
        var erase = cutting.Where(u => u.Title != null).SelectMany(u =>
            u.Title!.Cells.Where(c => c.Column > 0 && !Blank(c.Text)).Select(c =>
            {
                var (x0, y0, x1, y1) = CellRect(u.Table, c);
                return new Erase(u.P, x0, y0, x1, y1);
            })).ToList();
        return new Plan(pages, cutting, moving, dest, erase, endNeeded, project);
    }

    /// <summary>Partie d'une case (zone d'édition) à l'intérieur de sa colonne, en pixels entiers.</summary>
    private static (int X0, int Y0, int X1, int Y1) CellRect(Table table, Field f)
    {
        int c = f.Column;
        int x0 = Math.Max(R(table.ColumnXs[c]) + Margin, (int)Math.Floor(f.Box.Left));
        int x1 = Math.Min(R(table.ColumnXs[c + 1]) - Margin, (int)Math.Ceiling(f.Box.Right));
        return (x0, (int)Math.Floor(f.Box.Top), x1, (int)Math.Ceiling(f.Box.Bottom));
    }

    /// <summary>Ordonnée sur la page affichée d'une ordonnée de la page d'origine (null si ce morceau est parti ailleurs).</summary>
    public static int? MapY(PagePlan plan, int y)
    {
        foreach (var pc in plan.Pieces) if (pc.IsCopy && y >= pc.Y0 && y < pc.Y1) return pc.Dst + y - pc.Y0;
        var last = plan.Pieces.LastOrDefault(pc => pc.IsCopy);
        if (last != null && y >= last.Y1) return last.Dst + (last.Y1 - last.Y0) + (y - last.Y1);
        return null;
    }

    /// <summary>Inverse de <see cref="MapY"/> (zones verrouillées tracées à l'écran).</summary>
    public static int? UnmapY(PagePlan plan, int y)
    {
        foreach (var pc in plan.Pieces)
        {
            int h = pc.IsCopy ? pc.Y1 - pc.Y0 : pc.H;
            if (y >= pc.Dst && y < pc.Dst + h) return pc.IsCopy ? pc.Y0 + y - pc.Dst : null;
        }
        return y;
    }

    /// <summary>La page k change-t-elle de forme (rangées parties, arrivées ou déplacées) ?</summary>
    public static bool PageArranged(InventoryProject project, Plan plan, int k)
    {
        var pp = plan.Pages[k];
        return pp.Pieces.Count != 1 || !pp.Pieces[0].IsCopy || pp.Pieces[0].Y0 != 0 || pp.Height != project.Pages[k].Height
               || plan.Erase.Any(e => e.P == k);
    }

    /// <summary>Pages dont l'image est nécessaire pour composer la page k (elle-même ou son modèle, et celles des lots 0 recollés).</summary>
    public static List<int> SourcesOf(Plan plan, int k) =>
        new[] { plan.Pages[k].Template ?? k }.Concat(plan.Pages[k].Pieces.Where(pc => pc.IsUnit).Select(pc => pc.Unit!.P)).Distinct().ToList();

    // ---------- Placement des cases sur la page affichée ----------

    public sealed record ViewCell(Field Field, int Dx, int Dy, RectD Box, RectD Ink);

    public sealed class ViewLine
    {
        public PrintedLine Line { get; init; } = null!;
        public Page Page { get; init; } = null!;
        public Table Table { get; init; } = null!;
        public List<ViewCell> Cells { get; init; } = [];
        public int Dy { get; init; }
        public bool Moved { get; init; }
        public bool CarriedOnly { get; init; }
        public RectD? LotBox { get; init; }
    }

    public sealed record Strip(int R, double Top, double Bottom);
    /// <summary>Lot 0 recollé dans un tableau, tel qu'à l'écran (pour sa poignée en mode Modifier).</summary>
    public sealed record UnitStrip(Unit Unit, double Top, double Bottom);
    public sealed record ViewTable(Table Table, List<double> ColumnXs, List<double> RowYs, List<Strip> Strips, List<UnitStrip>? Units = null);
    public sealed record ViewPage(Page Page, int Width, int Height, List<ViewLine> Lines, List<ViewTable> Tables);

    private static int CenterY(PrintedLine line, Field f) => (int)Math.Floor(PageLayout.BaseBox(line, f).CenterY);
    private static RectD Shift(RectD b, double dx, double dy) => b with { X = b.X + dx, Y = b.Y + dy };

    private static ViewCell Place(Field f, int dx, int dy, RectD? clip)
    {
        var box = Shift(f.Box, dx, dy);
        if (clip is RectD c)
        {
            double x0 = Math.Max(box.Left, c.Left), y0 = Math.Max(box.Top, c.Top), x1 = Math.Min(box.Right, c.Right), y1 = Math.Min(box.Bottom, c.Bottom);
            if (x1 > x0 && y1 > y0) box = RectD.FromEdges(x0, y0, x1, y1);
        }
        return new ViewCell(f, dx, dy, box, f.Ink.IsEmpty ? f.Ink : Shift(f.Ink, dx, dy));
    }

    /// <summary>Ce qu'affiche la page k : lignes (avec le décalage de chaque case), tableaux (rangées à l'écran) et hauteur.</summary>
    public static ViewPage ViewOf(InventoryProject project, Plan plan, int k)
    {
        var page = project.Pages[k];
        var pp = plan.Pages[k];
        var lines = new List<ViewLine>();
        var moved = plan.Units.SelectMany(u => u.Lines).ToHashSet();
        var carried = new Dictionary<Field, Unit>();
        foreach (var u in plan.Units.Where(u => u.Title != null))
            foreach (var c in u.Title!.Cells.Where(c => c.Column > 0 && !Blank(c.Text))) carried[c] = u;
        bool lots = Lots.IsActive(project);
        double w = Lots.ColumnWidth(page);

        // Lignes restées sur leur page.
        foreach (var t in page.Tables)
            foreach (var r in Lots.RowOrder(t))
                foreach (var line in t.Rows[r].Lines)
                {
                    if (moved.Contains(line) || line.Cells.Count == 0) continue;
                    int anchor = CenterY(line, line.Cells[0]);
                    int? my = GridTable(t) ? MapY(pp, anchor) : anchor;
                    if (my is null) continue;
                    int dy = my.Value - anchor;
                    lines.Add(new ViewLine
                    {
                        Line = line, Page = page, Table = t, Dy = dy,
                        Cells = line.Cells.Where(f => !carried.ContainsKey(f)).Select(f => Place(f, 0, dy, null)).ToList(),
                        LotBox = lots && Lots.HasCell(line) ? Shift(Lots.NumberBox(t, line, w), 0, dy) : null,
                    });
                }
        // Lots 0 recollés ici.
        foreach (var pc in pp.Pieces.Where(pc => pc.IsUnit))
        {
            var u = pc.Unit!;
            var dt = u.Dest.Table;
            int dy = pc.Dst + Filet - u.BodyTop;
            double top = pc.Dst + Filet, bottom = pc.Dst + pc.H;
            RectD ClipCol(int c) => RectD.FromEdges(dt.ColumnXs[Math.Min(c, dt.ColumnXs.Count - 2)] + 1.5, top, dt.ColumnXs[Math.Min(c + 1, dt.ColumnXs.Count - 1)] - 1.5, bottom);
            int ShiftOf(int c) => c < u.Shift.Length ? u.Shift[c] : 0;
            foreach (var line in u.Lines)
            {
                RectD? lotBox = null;
                if (lots && Lots.HasCell(line))
                {
                    var b = Shift(Lots.NumberBox(u.Table, line, u.SrcLotWidth > 0 ? u.SrcLotWidth : w), 0, dy);
                    double right = Lots.TableLeft(dt), left = Math.Max(0, right - (u.DstLotWidth > 0 ? u.DstLotWidth : w));
                    lotBox = new RectD(left + 2, b.Y, Math.Max(4, right - left - 4), b.Height);
                }
                lines.Add(new ViewLine
                {
                    Line = line, Page = u.Page, Table = u.Table, Dy = dy, Moved = true, LotBox = lotBox,
                    Cells = line.Cells.Select(f => Place(f, ShiftOf(f.Column), dy, ClipCol(f.Column))).ToList(),
                });
            }
            if (u.Title != null)
            {
                var reference = u.Title.Cells.First(c => c.Column > 0 && !Blank(c.Text));
                int dyc = dy + CenterY(u.Object, u.Object.Cells[0]) - CenterY(u.Title, reference);
                lines.Add(new ViewLine
                {
                    Line = u.Title, Page = u.Page, Table = u.Table, Dy = dyc, Moved = true, CarriedOnly = true,
                    Cells = u.Title.Cells.Where(c => carried.TryGetValue(c, out var cu) && cu == u).Select(f => Place(f, ShiftOf(f.Column), dyc, ClipCol(f.Column))).ToList(),
                });
            }
        }
        // Tableaux tels qu'à l'écran : rangées dans l'ordre affiché (poignées, quadrillage des pages sans image).
        var tables = new List<ViewTable>();
        for (int ti = 0; ti < page.Tables.Count; ti++)
        {
            var t = page.Tables[ti];
            if (!GridTable(t)) { tables.Add(new ViewTable(t, t.ColumnXs, t.RowYs, [])); continue; }
            var strips = new List<Strip>();
            foreach (var r in Lots.RowOrder(t))
            {
                if (r + 1 >= t.RowYs.Count) continue;
                if (plan.Units.Any(u => u.P == k && u.Ti == ti && u.R == r && u.Whole)) continue;
                int? a = MapY(pp, CutY(t.RowYs[r]) + Cut);
                if (a is null) continue;
                int cutH = plan.Units.Where(u => u.P == k && u.Ti == ti && u.R == r && !u.Whole).Sum(u => u.Bottom - u.Top);
                strips.Add(new Strip(r, a.Value, a.Value + (CutY(t.RowYs[r + 1]) - CutY(t.RowYs[r])) - cutH));
            }
            var ys = strips.Select(s => s.Top).ToList();
            var units = page.EndLots ? [] : pp.Pieces.Where(pc => pc.IsUnit && pc.Unit!.Dest.P == k && pc.Unit.Dest.Ti == ti).ToList();
            ys.AddRange(units.Select(pc => (double)(pc.Dst + Cut)));
            int? end = MapY(pp, R(t.RowYs[^1]));
            if (end != null) ys.Add(end.Value);
            ys.Sort();
            // Lots 0 recollés dans ce tableau : déplaçables avec une poignée (mode Modifier).
            tables.Add(new ViewTable(t, t.ColumnXs, ys, strips, units.Where(pc => pc.Unit!.Kind != "deleted").Select(pc => new UnitStrip(pc.Unit!, pc.Dst + Cut, pc.Dst + pc.H)).ToList()));
        }
        return new ViewPage(page, page.Width, pp.Height, lines, tables);
    }

    // ---------- Pixels ----------

    /// <summary>Morceau d'image utile d'un lot 0 (lignes [OffsetY, OffsetY + Image.Height[ de sa page).</summary>
    public sealed record Crop(Raster Image, int OffsetY);

    public static Crop UnitCrop(Unit unit, Raster image)
    {
        int y0 = unit.BodyTop;
        if (unit.Title != null)
            foreach (var c in unit.Title.Cells.Where(c => c.Column > 0 && !Blank(c.Text))) y0 = Math.Min(y0, CellRect(unit.Table, c).Y0);
        y0 = Math.Max(0, y0);
        int y1 = Math.Min(image.Height, unit.Bottom);
        int stride = image.Width * image.Channels;
        int h = Math.Max(0, y1 - y0);
        var data = new byte[h * stride];
        if (h > 0) Array.Copy(image.Data, y0 * stride, data, 0, h * stride);
        return new Crop(new Raster(image.Width, h, image.Channels, data), y0);
    }

    private static int Lum(Raster img, int x, int y, int oy = 0)
    {
        int o = ((y - oy) * img.Width + x) * img.Channels;
        var d = img.Data;
        return img.Channels == 1 ? d[o] : (d[o] * 29 + d[o + 1] * 150 + d[o + 2] * 77) >> 8;
    }

    private static void Put(Raster dst, int dx, int dy, Raster src, int sx, int sy, int oy = 0)
    {
        int o = (dy * dst.Width + dx) * dst.Channels, i = ((sy - oy) * src.Width + sx) * src.Channels;
        if (src.Channels == dst.Channels) { Array.Copy(src.Data, i, dst.Data, o, dst.Channels); return; }
        if (dst.Channels == 1) { dst.Data[o] = (byte)Lum(src, sx, sy, oy); return; }
        byte g = src.Data[i];
        dst.Data[o] = g; dst.Data[o + 1] = g; dst.Data[o + 2] = g; dst.Data[o + 3] = 255;
    }

    private static void Paint(Raster dst, int x, int y, byte[] color)
    {
        int o = (y * dst.Width + x) * dst.Channels;
        for (int c = 0; c < dst.Channels; c++) dst.Data[o + c] = color[Math.Min(c, color.Length - 1)];
    }

    /// <summary>Couleur du papier sur une rangée de pixels : moyenne de la moitié la plus claire.</summary>
    private static byte[] PaperRow(Raster img, int y)
    {
        int w = img.Width, ch = img.Channels;
        var d = img.Data;
        var l = new List<(int L, int X)>(w);
        for (int x = 0; x < w; x++) { int o = (y * w + x) * ch; l.Add((ch == 1 ? d[o] : d[o] + d[o + 1] + d[o + 2], x)); }
        l.Sort((a, b) => b.L != a.L ? b.L.CompareTo(a.L) : a.X.CompareTo(b.X));
        int n = Math.Max(1, w / 2);
        var sum = new long[ch];
        for (int i = 0; i < n; i++) { int o = (y * w + l[i].X) * ch; for (int c = 0; c < ch; c++) sum[c] += d[o + c]; }
        return sum.Select(v => (byte)(v / n)).ToArray();
    }

    /// <summary>Couleur du papier autour d'un rectangle : moyenne de la moitié la plus claire de son pourtour.</summary>
    private static byte[] PaperAround(Raster img, Erase r)
    {
        var s = new List<(int L, int O)>();
        int x0 = Math.Max(0, r.X0), x1 = Math.Min(img.Width - 1, r.X1 - 1), y0 = Math.Max(0, r.Y0), y1 = Math.Min(img.Height - 1, r.Y1 - 1);
        void Add(int x, int y) => s.Add((Lum(img, x, y), (y * img.Width + x) * img.Channels));
        for (int x = x0; x <= x1; x++) { Add(x, y0); Add(x, y1); }
        for (int y = y0; y <= y1; y++) { Add(x0, y); Add(x1, y); }
        if (s.Count == 0) return Enumerable.Repeat((byte)255, img.Channels).ToArray();
        s.Sort((a, b) => b.L != a.L ? b.L.CompareTo(a.L) : a.O.CompareTo(b.O));
        int n = Math.Max(1, s.Count / 2);
        var sum = new long[img.Channels];
        for (int i = 0; i < n; i++) for (int c = 0; c < img.Channels; c++) sum[c] += img.Data[s[i].O + c];
        return sum.Select(v => (byte)(v / n)).ToArray();
    }

    /// <summary>
    /// Image de la page k telle qu'affichée : <paramref name="image"/> est l'image de la page (redressée, colonne des lots tracée,
    /// corrections dessinées pour l'export) ; <paramref name="crops"/> donne, pour chaque lot 0 recollé ici, son morceau d'image.
    /// </summary>
    public static Raster ComposePage(Plan plan, int k, Raster image, IReadOnlyDictionary<Unit, Crop?> crops)
    {
        var pp = plan.Pages[k];
        int w = image.Width, ch = image.Channels, stride = w * ch;
        var output = new Raster(w, pp.Height, ch, new byte[w * pp.Height * ch]);
        var white = Enumerable.Repeat((byte)255, ch).ToArray();
        var paper = PaperRow(image, Math.Min(image.Height - 1, 2));
        foreach (var pc in pp.Pieces)
        {
            if (pc.Paper)
            {
                for (int y = pc.Dst; y < Math.Min(output.Height, pc.Dst + pc.H); y++) for (int x = 0; x < w; x++) Paint(output, x, y, paper);
                continue;
            }
            if (pc.IsCopy)
            {
                int a = Math.Clamp(pc.Y0, 0, image.Height), b = Math.Clamp(pc.Y1, a, image.Height);
                Array.Copy(image.Data, a * stride, output.Data, pc.Dst * stride, (b - a) * stride);
                for (int y = pc.Dst + (b - a); y < pc.Dst + (pc.Y1 - pc.Y0); y++) for (int x = 0; x < w; x++) Paint(output, x, y, white);
            }
            else Synthesize(output, pc, image, crops.TryGetValue(pc.Unit!, out var c) ? c : null);
        }
        // Valeurs de titre parties avec leur objet : papier à la place.
        foreach (var e in plan.Erase.Where(e => e.P == k))
        {
            var around = PaperAround(image, e);
            for (int y = e.Y0; y < e.Y1; y++)
            {
                int? my = MapY(pp, y);
                if (my is null || my < 0 || my >= output.Height) continue;
                for (int x = Math.Max(0, e.X0); x < Math.Min(w, e.X1); x++) Paint(output, x, my.Value, around);
            }
        }
        if (plan.Project is { } project && k < project.Pages.Count && !project.Pages[k].EndLots) CloseLotColumns(project, project.Pages[k], pp, image, output, paper);
        return output;
    }

    /// <summary>
    /// Colonne des lots après les déplacements (rangée déplacée, lot 0 parti) : chaque rangée tracée garde son cadre complet
    /// (haut, bas, gauche) là où elle arrive, et le trait laissé par une rangée partie disparaît du haut des autres rangées.
    /// </summary>
    private static void CloseLotColumns(InventoryProject project, Page page, PagePlan pp, Raster image, Raster output, byte[] paper)
    {
        if (!project.LotFrame) return;
        double w = Lots.ColumnWidth(page);
        var units = pp.Pieces.Where(pc => pc.IsUnit).ToList();
        foreach (var t in LotColumn.FramedTables(page))
        {
            if (t.Rules is null) continue;
            int right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
            if (right - left < 8) continue;
            int vT = t.Rules.V, hT = t.Rules.H, hv = vT / 2, hh = hT / 2;
            var color = t.Rules.Color.Select(b => (byte)b).ToArray();
            // Rangée r sur la page affichée : [début, fin[ de sa bande (null si elle est partie entière).
            (int S, int E)? Strip(int r)
            {
                int a = CutY(t.RowYs[r]), b = CutY(t.RowYs[r + 1]);
                int? s = null, e = null;
                foreach (var pc in pp.Pieces)
                {
                    if (!pc.IsCopy) continue;
                    int y0 = Math.Max(a, pc.Y0), y1 = Math.Min(b, pc.Y1);
                    if (y1 <= y0) continue;
                    int d0 = pc.Dst + y0 - pc.Y0;
                    if (s is null || d0 < s) s = d0;
                    if (e is null || d0 + y1 - y0 > e) e = d0 + y1 - y0;
                }
                return s is null ? null : (s.Value, e!.Value);
            }
            var framed = LotColumn.FramedRows(t).ToHashSet();
            var bands = new List<(int Y0, int Y1, int S, int E)>();
            foreach (int r in framed) if (Strip(r) is { } st) bands.Add((st.S + Cut - hh, st.E + Cut - hh + hT, st.S, st.E));
            bool Inside(int y) => bands.Any(b => y >= b.Y0 && y < b.Y1) || units.Any(u => y >= u.Dst && y < u.Dst + u.H);
            // Haut des rangées sans cadre : le trait d'une rangée tracée qui n'est plus là est effacé (encre seulement).
            foreach (int r in Lots.RowOrder(t))
            {
                if (r + 1 >= t.RowYs.Count || framed.Contains(r)) continue;
                if (Strip(r) is not { } st) continue;
                int? rule = LotColumn.RuleLeftOf(image, right, R(t.RowYs[r]) + hh + 1, R(t.RowYs[r + 1]) - hh);
                int xEnd = Math.Min(right - vT, rule ?? right);
                for (int y = st.S; y < Math.Min(Math.Min(st.E, st.S + Cut - hh + hT + 1), output.Height); y++)
                {
                    if (y < 0 || Inside(y)) continue;
                    for (int x = Math.Max(0, left - hv - 1); x < xEnd; x++) if (Lum(output, x, y) < 160) Paint(output, x, y, paper);
                }
            }
            // Cadre de chaque rangée tracée, à sa place d'arrivée.
            foreach (var b in bands)
            {
                int top = b.S + Cut, bottom = b.E + Cut;
                LotColumn.Fill(output, left - hv, top - hh, vT, bottom - top + hT, color);
                LotColumn.Fill(output, left - hv, top - hh, right - (left - hv), hT, color);
                LotColumn.Fill(output, left - hv, bottom - hh, right - (left - hv), hT, color);
            }
        }
    }

    /// <summary>Rangée refaite pour un lot 0 : filet et fond du tableau d'arrivée, contenu des cases recopié colonne par colonne.</summary>
    private static void Synthesize(Raster output, Piece pc, Raster image, Crop? crop)
    {
        var u = pc.Unit!;
        var d = u.Dest; var dt = d.Table; var st = u.Table;
        int w = output.Width;
        int fs = CutY(dt.RowYs[d.R]), re = CutY(dt.RowYs[d.R + 1]);
        // Filet du haut : celui de la rangée d'arrivée.
        for (int k = 0; k < Filet; k++)
        {
            int sy = Math.Clamp(fs + k, 0, image.Height - 1);
            for (int x = 0; x < w; x++) Put(output, x, pc.Dst + k, image, x, sy);
        }
        // Fond : l'interligne le plus propre de la rangée d'arrivée (filets verticaux prolongés, papier ailleurs).
        int x0 = Math.Max(0, R(dt.ColumnXs[0]) - (u.DstLotWidth > 0 ? u.DstLotWidth + 8 : 4)), x1 = Math.Min(w, R(dt.ColumnXs[^1]) + 4);
        int yc = Math.Clamp(fs + Filet, 0, image.Height - 1);
        long best = -1;
        for (int y = fs + Filet; y < re; y++)
        {
            if (y < 0 || y >= image.Height) continue;
            long s = 0;
            for (int x = x0; x < x1; x++) s += Lum(image, x, y);
            if (s > best) { best = s; yc = y; }
        }
        var paper = PaperRow(image, yc);
        int bodyH = pc.H - Filet;
        for (int k = 0; k < bodyH; k++)
        {
            int y = pc.Dst + Filet + k;
            for (int x = 0; x < w; x++)
            {
                if (x >= x0 && x < x1 && Lum(image, x, yc) < 150) Put(output, x, y, image, x, yc);
                else Paint(output, x, y, paper);
            }
        }
        if (crop is null) return;
        int dy = pc.Dst + Filet - u.BodyTop;
        var ci = crop.Image;
        void Blit(int sx0, int sx1, int sy0, int sy1, int dx, int ddy, int cx0, int cx1)
        {
            for (int y = Math.Max(sy0, crop.OffsetY); y < Math.Min(sy1, crop.OffsetY + ci.Height); y++)
            {
                int ty = y + ddy;
                if (ty < pc.Dst + Filet || ty >= pc.Dst + pc.H) continue;
                for (int x = Math.Max(0, sx0); x < Math.Min(ci.Width, sx1); x++)
                {
                    int tx = x + dx;
                    if (tx < cx0 || tx >= cx1 || tx < 0 || tx >= w) continue;
                    Put(output, tx, ty, ci, x, y, crop.OffsetY);
                }
            }
        }
        // Contenu de chaque colonne, recalé sur la colonne d'arrivée.
        int nd = dt.ColumnXs.Count - 1;
        for (int c = 0; c < st.ColumnXs.Count - 1; c++)
        {
            int dc = Math.Min(c, nd - 1);
            Blit(R(st.ColumnXs[c]) + Margin, R(st.ColumnXs[c + 1]) - Margin, u.BodyTop, u.Bottom, u.Shift[c], dy,
                R(dt.ColumnXs[dc]) + Margin, R(dt.ColumnXs[dc + 1]) - Margin);
        }
        // Case du numéro de lot.
        if (u.SrcLotWidth > 0 && u.DstLotWidth > 0)
        {
            int sl = R(Lots.TableLeft(st)), dl = R(Lots.TableLeft(dt));
            Blit(sl - u.SrcLotWidth + Margin, sl - Margin, u.BodyTop, u.Bottom, u.LotShift, dy, dl - u.DstLotWidth + Margin, dl - Margin);
        }
        // Valeurs du titre reprises par l'objet : à la hauteur de l'objet.
        if (u.Title != null)
        {
            var reference = u.Title.Cells.First(c => c.Column > 0 && !Blank(c.Text));
            int dyc = dy + CenterY(u.Object, u.Object.Cells[0]) - CenterY(u.Title, reference);
            foreach (var f in u.Title.Cells)
            {
                if (f.Column == 0 || Blank(f.Text)) continue;
                var (rx0, ry0, rx1, ry1) = CellRect(st, f);
                int dc = Math.Min(f.Column, nd - 1);
                Blit(rx0, rx1, ry0, ry1, f.Column < u.Shift.Length ? u.Shift[f.Column] : 0, dyc, R(dt.ColumnXs[dc]) + Margin, R(dt.ColumnXs[dc + 1]) - Margin);
            }
        }
    }
}
