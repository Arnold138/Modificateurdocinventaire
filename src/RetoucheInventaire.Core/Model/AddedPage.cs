using System.Text.Json;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Storage;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Page ajoutée (« Ajouter une page ») : page vierge sur le modèle d'une page de l'inventaire, pour les objets inventoriés
/// à la main. Mêmes tableaux, mêmes rangées, mêmes filets et mêmes en-têtes que le modèle ; tout le reste est vide.
/// L'image vient de la même source que le modèle (même cadrage), « nettoyée » : dans les tableaux d'objets, papier propre et
/// filets retracés (recopiés de l'imprimé, voir <see cref="BlankBase"/>) ; en-têtes gardés tels qu'imprimés. On peut ensuite
/// ajouter ou retirer des colonnes, des lignes (menu « Ligne », comme ailleurs) et des lignes plus hautes (Tall).
/// Portage identique de web/core/addpage.js (même image au pixel près sur Windows et sur iPhone/iPad).
/// </summary>
public static partial class AddedPage
{
    private const int Dark = 150;
    /// <summary>Plus petite largeur d'une colonne (pixels de page) : en dessous, on ne la coupe plus en deux.</summary>
    public const int MinColumn = 36;

    private static int R(double v) => (int)Math.Floor(v + 0.5);
    private static bool GridTable(Table t) => t.HasGrid && t.RowYs.Count >= 2 && t.ColumnXs.Count >= 2;

    /// <summary>Tableau vidé : à filets, au moins trois colonnes dont une large (la désignation) ; pas un cadre de titre tramé.</summary>
    private static bool Blankable(Table t)
    {
        if (!GridTable(t) || t.ColumnXs.Count < 4) return false;
        double widest = 0;
        for (int c = 0; c + 1 < t.ColumnXs.Count; c++) widest = Math.Max(widest, t.ColumnXs[c + 1] - t.ColumnXs[c]);
        return widest * 100 >= 35 * (t.ColumnXs[^1] - t.ColumnXs[0]);
    }

    /// <summary>Hauteur sous laquelle une rangée ne peut pas contenir une ligne de texte (bande d'un fond tramé).</summary>
    private static double EmOf(Table t)
    {
        var sizes = t.Rows.SelectMany(r => r.Lines).SelectMany(l => l.Cells).Select(f => f.FontSize).Where(v => v > 0).OrderBy(v => v).ToList();
        return sizes.Count > 0 ? sizes[sizes.Count / 2] : 12;
    }

    private static double ThinOf(Table t) => Math.Max(10, EmOf(t) * 1.1);

    /// <summary>Ligne vierge qui occupe toute la rangée r (une case par colonne), comme une rangée sans texte lu.</summary>
    private static PrintedLine FreshLine(Table t, int r, double em)
    {
        double top = t.RowYs[r], bottom = t.RowYs[r + 1];
        var line = new PrintedLine { Kind = LineKind.Article };
        for (int c = 0; c + 1 < t.ColumnXs.Count; c++)
        {
            var box = RectD.FromEdges(t.ColumnXs[c] + 4, top + 4, t.ColumnXs[c + 1] - 4, Math.Max(top + 5, bottom - 4));
            line.Cells.Add(new Field
            {
                Column = c, Box = box, Align = c == 0 ? TextAlign.Left : TextAlign.Right, Indent = 4,
                FontSize = Math.Min(em, Math.Max(6, box.Height / 1.05)),
            });
        }
        return line;
    }

    private static double RowHeight(Table t, int r) => t.RowYs[r + 1] - t.RowYs[r];

    [GeneratedRegex(@"^\s*(?:page\s*)?[-–]?\s*\d{1,3}\s*(?:[/sur]+\s*\d{1,3})?\s*[-–]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberRx();
    [GeneratedRegex(@"[^\p{L}\p{N}/ -]")]
    private static partial Regex NotPageCharRx();

    private static bool IsPageNumber(PrintedLine l)
    {
        var t = NotPageCharRx().Replace(string.Join(" ", l.Cells.Select(c => c.Text)), " ").Trim();
        return t.Length > 0 && PageNumberRx().IsMatch(t);
    }

    /// <summary>La page peut servir de modèle : une page image (pas « Lots de fin ») avec au moins un tableau d'objets.</summary>
    public static bool CanBeModel(Page? page) => page != null && page.Source == PageSource.Image && !page.EndLots && page.Tables.Any(Blankable);

    /// <summary>Rangées d'en-tête au début du tableau : toutes leurs lignes écrites sont des en-têtes, ou bandes fines d'un fond tramé.</summary>
    private static int HeadRows(Table t)
    {
        double thin = ThinOf(t);
        int h = 0;
        for (; h < t.Rows.Count && h + 1 < t.RowYs.Count; h++)
        {
            var written = t.Rows[h].Lines.Where(l => l.FirstText.Length > 0 || l.Kind == LineKind.EnTete).ToList();
            bool head = written.Count > 0 && written.All(l => l.Kind == LineKind.EnTete);
            if (!head && RowHeight(t, h) >= thin) break;
        }
        return Math.Min(h, t.Rows.Count - 1);
    }

    private static void DropRule(Table t, int k)
    {
        t.Rows[k - 1].Lines.AddRange(t.Rows[k].Lines);
        t.Rows.RemoveAt(k);
        t.RowYs.RemoveAt(k);
    }

    /// <summary>Corps du tableau : les bandes fines (total sur fond tramé lu comme plusieurs filets) sont réunies en une rangée.</summary>
    private static void MergeThinBody(Table t, int head)
    {
        double thin = ThinOf(t), em = EmOf(t);
        var merged = new HashSet<Row>();
        for (int guard = 0; guard < 500; guard++)
        {
            int r = head;
            while (r < t.Rows.Count && !(RowHeight(t, r) < thin)) r++;
            if (r >= t.Rows.Count || t.Rows.Count - head <= 1) break;
            int keep = r + 1 < t.Rows.Count ? r : r - 1;
            DropRule(t, keep + 1);
            merged.Add(t.Rows[keep]);
        }
        for (int r = head; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            var real = row.Lines.Where(l => l.Cells.Count > 0 && l.Cells[0].Box.Height >= em * 0.5).ToList();
            row.Lines = merged.Contains(row) || real.Count == 0 ? [FreshLine(t, r, em)] : real;
        }
    }

    /// <summary>Filet vertical bien présent sur l'image entre y0 et y1 (au moins la moitié) ; faux pour une colonne lue en trop.</summary>
    private static bool RuleThere(Raster img, double xd, double y0d, double y1d)
    {
        int x = R(xd), y0 = Math.Max(0, R(y0d)), y1 = Math.Min(img.Height, R(y1d));
        if (y1 <= y0) return true;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            bool dark = false;
            for (int d = -2; d <= 2 && !dark; d++) if (x + d >= 0 && x + d < img.Width && PageLayout.Gray(img, x + d, y) < Dark) dark = true;
            if (dark) n++;
        }
        return n * 2 >= y1 - y0;
    }

    private static void DropOuterColumn(Table t, bool first)
    {
        t.ColumnXs.RemoveAt(first ? 0 : t.ColumnXs.Count - 1);
        int gone = first ? 0 : t.ColumnXs.Count - 1;
        foreach (var row in t.Rows)
            foreach (var l in row.Lines)
            {
                l.Cells = l.Cells.Where(f => f.Column != gone).ToList();
                if (first) foreach (var f in l.Cells) f.Column--;
            }
    }

    private static void RowBounds(Table t)
    {
        double l = t.ColumnXs[0], r = t.ColumnXs[^1];
        for (int i = 0; i < t.Rows.Count && i + 1 < t.RowYs.Count; i++) t.Rows[i].Bounds = RectD.FromEdges(l, t.RowYs[i], r, t.RowYs[i + 1]);
    }

    private static void ClearField(Field f)
    {
        f.Text = ""; f.OriginalText = ""; f.Ink = RectD.Empty; f.Strikes = []; f.Colors = []; f.Manual = false; f.Locked = false;
    }

    private static void ClearLine(PrintedLine l, bool force)
    {
        foreach (var f in l.Cells) ClearField(f);
        l.Kind = LineKind.Article; l.KindChosenByUser = false; l.Lot = ""; l.LotManual = false; l.LotSlot = false; l.NoLot = false;
        l.ForceLot = false; l.AutoLot = force; l.LotHere = false; l.Extra = 0; l.ExtraAt = 0; l.Copy = false; l.Deleted = false; l.TotalScope = null; l.EndAt = null; l.Tall = 0;
    }

    private static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x, ProjectStore.Json), ProjectStore.Json)!;

    /// <summary>
    /// Crée la page vierge sur le modèle de <paramref name="model"/> et l'insère à la position <paramref name="at"/>.
    /// <paramref name="img"/> : image de base du modèle (colonnes du bord lues en trop retirées). Null si le modèle ne convient pas.
    /// </summary>
    /// <summary>
    /// Rangée modèle d'un tableau : une rangée d'une seule ligne, de hauteur médiane parmi elles (pas l'en-tête, ni une ligne
    /// agrandie, ni un total ajouté). C'est elle que « Ajouter des lignes » recopie (vierge). Null s'il n'y en a pas.
    /// </summary>
    public static int? ReferenceRow(Table t, int head = 0)
    {
        var c = new List<(int R, double H)>();
        for (int r = head; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
        {
            var ls = t.Rows[r].Lines;
            if (ls.Count != 1 || ls[0].Cells.Count == 0 || ls[0].Deleted || ls[0].TotalScope != null || ls[0].Kind == LineKind.EnTete || ls[0].Extra > 0 || ls[0].Tall > 0) continue;
            if (ls[0].Kind is not (LineKind.Article or LineKind.Titre)) continue;
            c.Add((r, t.RowYs[r + 1] - t.RowYs[r]));
        }
        if (c.Count == 0) return null;
        c.Sort((a, b) => a.H != b.H ? a.H.CompareTo(b.H) : a.R.CompareTo(b.R));
        return c[(c.Count - 1) / 2].R;
    }

    private static int HeadOf(Page page, Table t)
    {
        int ti = page.Tables.IndexOf(t);
        return page.Added != null && ti >= 0 && ti < page.Added.Tables.Count && page.Added.Tables[ti] is { } x ? x.Head : HeadRows(t);
    }

    /// <summary>
    /// Crée la page vierge sur le modèle de <paramref name="model"/> et l'insère à la position <paramref name="at"/>.
    /// <paramref name="empty"/> : page blanche — seulement le 1er tableau d'objets, son en-tête et une ligne (la rangée modèle).
    /// </summary>
    public static Page? AddBlankPage(InventoryProject project, Page model, int at, Raster? img = null, string? id = null, bool empty = false)
    {
        if (!CanBeModel(model)) return null;
        var page = Clone(model);
        // 1. Page d'origine, sans ce qui a été ajouté ou retiré à la main.
        foreach (var l in page.AllLines().ToList()) PageLayout.SetExtra(page, l, 0);
        for (int guard = 0; guard < 1000; guard++)
        {
            var t = page.Tables.FirstOrDefault(x => x.Rows.Any(r => r.Inserted != null));
            if (t is null) break;
            var row = t.Rows.Last(r => r.Inserted != null);
            if (row.Lines.Count == 0 || !Rows.RemoveAdded(page, row.Lines[0])) row.Inserted = null;
        }
        foreach (var b in page.Inserts.OrderByDescending(b => b.At).ToList()) PageLayout.RemoveBand(page, b);
        page.Inserts = [];
        page.Height = PageLayout.BaseHeight(model);
        page.LockZones = [];
        foreach (var t in page.Tables) { t.RowOrder = null; if (GridTable(t)) RowBounds(t); }
        // 2. Tableaux d'objets vidés (en-têtes gardés), numéros de page effacés.
        var info = model.Added != null ? Clone(model.Added) : new AddedInfo();
        page.Id = id ?? Guid.NewGuid().ToString("N")[..8];
        for (int ti = 0; ti < page.Tables.Count; ti++)
        {
            var t = page.Tables[ti];
            AddedTable? at_ = model.Added != null ? (ti < info.Tables.Count ? info.Tables[ti] : null) : null;
            if (model.Added == null && Blankable(t))
            {
                int head = HeadRows(t);
                var orig = t.ColumnXs.ToList();
                MergeThinBody(t, head);
                if (img != null)
                {
                    double y0 = t.RowYs[head], y1 = t.RowYs[^1];
                    while (t.ColumnXs.Count > 3 && !RuleThere(img, t.ColumnXs[^1], y0, y1)) DropOuterColumn(t, false);
                    while (t.ColumnXs.Count > 3 && !RuleThere(img, t.ColumnXs[0], y0, y1)) DropOuterColumn(t, true);
                }
                at_ = new AddedTable { Ys = t.RowYs.ToList(), Head = head, Orig = orig };
                RowBounds(t);
            }
            while (info.Tables.Count <= ti) info.Tables.Add(null);
            info.Tables[ti] = at_;
            if (at_ is null) continue;
            for (int r = 0; r < t.Rows.Count; r++)
            {
                if (r >= at_.Head) { for (int i = 0; i < t.Rows[r].Lines.Count; i++) ClearLine(t.Rows[r].Lines[i], i == 0); }
                else
                    foreach (var l in t.Rows[r].Lines.Where(l => l.Kind != LineKind.EnTete && !l.KindChosenByUser))
                        foreach (var f in l.Cells) { f.Text = ""; f.OriginalText = ""; f.Locked = true; }
            }
        }
        if (info.Tables.Count > page.Tables.Count) info.Tables.RemoveRange(page.Tables.Count, info.Tables.Count - page.Tables.Count);
        if (empty)
        {
            // Page blanche : le 1er tableau d'objets garde son en-tête et une seule ligne (la rangée modèle, posée sous l'en-tête) ;
            // les tableaux en dessous disparaissent, et tout ce qui est sous cette ligne devient papier.
            int ti0 = info.Tables.FindIndex(x => x != null);
            var t0 = page.Tables[ti0];
            var x0 = info.Tables[ti0]!;
            int head = x0.Head;
            double top = t0.RowYs[head];
            int r0 = ReferenceRow(t0, head) ?? Math.Min(head, t0.Rows.Count - 1);
            double h = t0.RowYs[r0 + 1] - t0.RowYs[r0], dy = top - t0.RowYs[r0];
            var row = t0.Rows[r0];
            foreach (var l in row.Lines) foreach (var f in l.Cells) f.Box = f.Box with { Y = f.Box.Y + dy };
            t0.Rows = [.. t0.Rows.Take(head), row];
            t0.RowYs = [.. t0.RowYs.Take(head + 1), top + h];
            RowBounds(t0);
            x0.Ys = t0.RowYs.ToList();
            double t0Top = t0.RowYs[0];
            var keep = page.Tables.Select((t, ti) => ti == ti0 || !GridTable(t) || t.RowYs[0] < t0Top).ToList();
            page.Tables = page.Tables.Where((t, ti) => keep[ti]).ToList();
            info.Tables = info.Tables.Where((x, ti) => keep[ti]).ToList();
            info.Tail = top + h;
        }
        // Sous le dernier tableau vidé (total général, pied de page) : page vide.
        if (info.Tail is null)
        {
            var bottoms = info.Tables.Where(x => x != null).Select(x => x!.Ys[^1]).ToList();
            info.Tail = bottoms.Count > 0 ? bottoms.Max() : null;
        }
        if (info.Tail is double tail)
        {
            bool Below(PrintedLine l)
            {
                var b = l.Cells.Aggregate(RectD.Empty, (u, c) => u.Union(c.Box));
                return !b.IsEmpty && b.Y + b.Height / 2 > tail + 4;
            }
            foreach (var l in page.LooseLines.Where(Below)) foreach (var f in l.Cells) ClearField(f);
            for (int ti = 0; ti < page.Tables.Count; ti++)
                if (info.Tables[ti] is null)
                    foreach (var l in page.Tables[ti].Rows.SelectMany(r => r.Lines).Where(Below)) foreach (var f in l.Cells) ClearField(f);
        }
        void Erase(PrintedLine l)
        {
            var ink = l.Cells.Aggregate(RectD.Empty, (u, c) => u.Union(c.Ink));
            var z = ink.IsEmpty ? RectD.Empty : ink.Inflate(3, 3);
            // Jamais sur un tableau : la zone s'arrête au-dessus de son trait du haut (et d'un double trait).
            foreach (var t in page.Tables)
            {
                if (!GridTable(t) || z.IsEmpty) continue;
                double l0 = t.ColumnXs[0], r0 = t.ColumnXs[^1], y0 = t.RowYs[0], y1 = t.RowYs[^1];
                if (z.X >= r0 || z.X + z.Width <= l0 || z.Y >= y1 || z.Y + z.Height <= y0 - 12) continue;
                z = RectD.FromEdges(z.X, z.Y, z.X + z.Width, Math.Min(z.Y + z.Height, y0 - 12));
            }
            if (!z.IsEmpty) info.Erase.Add(z);
            foreach (var f in l.Cells) ClearField(f);
        }
        foreach (var l in page.LooseLines.Where(IsPageNumber)) Erase(l);
        foreach (var t in page.Tables.Where(t => !GridTable(t)))
            foreach (var l in t.Rows.SelectMany(r => r.Lines).Where(IsPageNumber).ToList()) Erase(l);
        info.From = model.Id;
        info.Next = 0;
        page.Added = info;
        page.EndLots = false;
        page.SourceName = $"{(empty ? "Page blanche" : "Page ajoutée")} (modèle : page {project.Pages.IndexOf(model) + 1})";
        PageAnalyzer.AssignIds(page);
        project.Pages.Insert(Math.Clamp(at, 0, project.Pages.Count), page);
        return page;
    }

    // ---------- Ajouter des lignes (1, 5, 10, 15…) ----------

    /// <summary>Bas de ce qui est écrit ou tracé sur la page (tableaux, textes, zones verrouillées).</summary>
    private static double ContentBottom(Page page)
    {
        double b = 0;
        foreach (var t in page.Tables) if (GridTable(t)) b = Math.Max(b, t.RowYs[^1]);
        foreach (var l in page.AllLines()) foreach (var f in l.Cells) if (f.Text.Trim().Length > 0 || !f.Ink.IsEmpty) b = Math.Max(b, f.Box.Y + f.Box.Height);
        foreach (var z in page.LockZones) b = Math.Max(b, z.Y + z.Height);
        return b;
    }

    public sealed record RowsResult(int Added, List<Page> Pages, PrintedLine? Last);

    /// <summary>
    /// « Ajouter des lignes » : <paramref name="n"/> rangées vierges sous (ou, pour la 1re, au-dessus de) la rangée de
    /// <paramref name="line"/>, chacune recopiée de la rangée modèle du tableau. La page garde sa taille (la place est prise dans
    /// le blanc du bas) ; quand elle est pleine, la suite part sur une page blanche ajoutée juste après.
    /// </summary>
    public static RowsResult AddRows(InventoryProject project, Page page, PrintedLine line, int n, bool above = false, Raster? img = null, string? id = null)
    {
        int added = 0;
        var pages = new List<Page>();
        PrintedLine? last = null;
        var cur = page;
        var dest = line;
        bool first = above;
        for (int guard = 0; added < n && guard < 4 * n + 10; guard++)
        {
            var at = Rows.Locate(cur, dest);
            if (at is null) break;
            var t = at.Table;
            int? r0 = ReferenceRow(t, HeadOf(cur, t));
            var reference = r0 is int rr ? t.Rows[rr].Lines[0] : dest;
            int H = PageLayout.BaseHeight(cur), margin = Math.Max(40, (int)Math.Round(H * 0.025, MidpointRounding.AwayFromZero));
            int before = cur.Height;
            var made = Rows.Copy(cur, dest, blank: true, above: first, from: reference, force: true); // aussi sous une ligne encore vide
            if (made is null) break;
            made.Copy = false; // case de lot quand on écrit dans la ligne (pas de numéros sur des lignes vides)
            bool fits = ContentBottom(cur) <= H - margin;
            // Page pleine mais pas de page suivante possible (tableau trop modifié pour servir de modèle) : la page s'allonge.
            if (fits || !CanBeModel(cur) || pages.Count > 20)
            {
                // La page garde sa taille : autant de blanc retiré en bas qu'il a été ajouté.
                int k = fits ? Math.Min(cur.Height - before, cur.Height - H) : 0;
                if (k > 0) PageLayout.CutBand(cur, cur.Height - k, k, null);
                added++; last = made; dest = made; first = false;
                continue;
            }
            Rows.RemoveAdded(cur, made); // plus de place : cette ligne part sur la page suivante
            var next = AddBlankPage(project, cur, project.Pages.IndexOf(cur) + 1, cur.Added != null ? null : img, id != null ? $"{id}-{pages.Count + 1}" : null, true);
            if (next is null) break;
            pages.Add(next);
            cur = next;
            var t0 = next.Tables[next.Added!.Tables.FindIndex(x => x != null)];
            dest = t0.Rows[^1].Lines[0];
            added++; last = dest; first = false; // la page blanche porte déjà une ligne
        }
        return new RowsResult(added, pages, last);
    }

    /// <summary>Tableau vidé de la page ajoutée (on peut y ajouter ou retirer des colonnes).</summary>
    public static bool AddedTableOf(Page page, Table t)
    {
        int ti = page.Tables.IndexOf(t);
        return page.Added != null && ti >= 0 && ti < page.Added.Tables.Count && page.Added.Tables[ti] != null;
    }

    private static string NextId(Page page) => $"{page.Id}-a{page.Added!.Next++}";

    /// <summary>Case d'en-tête dont l'imprimé ne correspond plus (colonne coupée ou réunie) : son texte devient tapé.</summary>
    private static void TypeHeader(Field f)
    {
        if (f.Text == f.OriginalText) f.OriginalText = "";
        f.Ink = RectD.Empty;
    }

    public static bool CanAddColumn(Page page, Table t, int c) =>
        AddedTableOf(page, t) && c >= 0 && c + 1 < t.ColumnXs.Count && t.ColumnXs[c + 1] - t.ColumnXs[c] >= 2 * MinColumn;

    public static bool CanRemoveColumn(Page page, Table t, int k) =>
        AddedTableOf(page, t) && k >= 1 && k + 1 < t.ColumnXs.Count && t.ColumnXs.Count > 3;

    /// <summary>Coupe la colonne c en deux (filet tracé au milieu) : une case vide apparaît sur chaque ligne.</summary>
    public static bool AddColumn(Page page, Table t, int c)
    {
        if (!CanAddColumn(page, t, c)) return false;
        double a = t.ColumnXs[c], b = t.ColumnXs[c + 1];
        int x = R((a + b) / 2);
        t.ColumnXs.Insert(c + 1, x);
        int head = page.Added!.Tables[page.Tables.IndexOf(t)]!.Head;
        for (int r = 0; r < t.Rows.Count; r++)
            foreach (var l in t.Rows[r].Lines)
            {
                var f = l.Cells.FirstOrDefault(q => q.Column == c);
                foreach (var q in l.Cells) if (q.Column > c) q.Column++;
                if (f is null) continue;
                double insetL = Math.Max(1.5, f.Box.X - a), insetR = Math.Max(1.5, b - f.Box.Right);
                var left = RectD.FromEdges(f.Box.X, f.Box.Y, x - insetR, f.Box.Bottom);
                var box = RectD.FromEdges(x + insetL, f.Box.Y, b - insetR, f.Box.Bottom);
                f.Box = left;
                if (r < head) TypeHeader(f); else f.Ink = RectD.Empty;
                var nf = new Field
                {
                    Id = NextId(page), Column = c + 1, OriginalText = "", Text = "", Ink = RectD.Empty, Box = box,
                    Align = c == 0 ? TextAlign.Left : f.Align, Indent = f.Indent, FontSize = f.FontSize,
                };
                l.Cells.Insert(l.Cells.IndexOf(f) + 1, nf);
            }
        RowBounds(t);
        return true;
    }

    /// <summary>Retire le filet intérieur k : les colonnes k−1 et k n'en font plus qu'une (textes réunis).</summary>
    public static bool RemoveColumn(Page page, Table t, int k)
    {
        if (!CanRemoveColumn(page, t, k)) return false;
        t.ColumnXs.RemoveAt(k);
        int head = page.Added!.Tables[page.Tables.IndexOf(t)]!.Head;
        for (int r = 0; r < t.Rows.Count; r++)
            foreach (var l in t.Rows[r].Lines)
            {
                var f1 = l.Cells.FirstOrDefault(q => q.Column == k - 1);
                var f2 = l.Cells.FirstOrDefault(q => q.Column == k);
                if (f1 != null && f2 != null)
                {
                    string text = string.Join(" ", new[] { f1.Text.Trim(), f2.Text.Trim() }.Where(s => s.Length > 0));
                    if (r < head) TypeHeader(f1); else f1.Ink = RectD.Empty;
                    f1.Text = text;
                    f1.Box = RectD.FromEdges(f1.Box.X, f1.Box.Y, f2.Box.Right, Math.Max(f1.Box.Bottom, f2.Box.Bottom));
                    f1.Strikes = []; f1.Colors = [];
                    l.Cells.Remove(f2);
                }
                else if (f2 != null) f2.Column = k - 1;
                foreach (var q in l.Cells) if (q.Column > k) q.Column--;
            }
        RowBounds(t);
        return true;
    }

    // ---------- Image ----------

    private static int CleanCol(Raster img, int x0, int x1, int y0, int y1, IEnumerable<int> ys, List<int> cols)
    {
        var near = new bool[img.Height];
        var skip = new bool[img.Width];
        foreach (var y in ys) for (int v = Math.Max(0, y - 3); v <= Math.Min(img.Height - 1, y + 3); v++) near[v] = true;
        foreach (var c in cols) for (int v = Math.Max(0, c - 5); v <= Math.Min(img.Width - 1, c + 5); v++) skip[v] = true;
        y0 = Math.Max(0, y0); y1 = Math.Min(img.Height, y1);
        int best = Math.Max(0, Math.Min(img.Width - 1, x0)), bestScore = int.MinValue;
        for (int x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
        {
            if (skip[x]) continue;
            int score = 0;
            for (int y = y0; y < y1; y++) if (PageLayout.Gray(img, x, y) < Dark) score += near[y] ? 1 : -1;
            if (score > bestScore) { bestScore = score; best = x; }
        }
        return best;
    }

    /// <summary>Pixels sombres d'un filet (décalage −3…+3 → valeurs), mesurés en travers du filet.</summary>
    private static List<(int D, byte[] V)> Profile(Raster img, int x, int y, bool vertical)
    {
        var o = new List<(int, byte[])>();
        for (int d = -3; d <= 3; d++)
        {
            int xx = vertical ? x + d : x, yy = vertical ? y : y + d;
            if (xx < 0 || yy < 0 || xx >= img.Width || yy >= img.Height || PageLayout.Gray(img, xx, yy) >= Dark) continue;
            int at = (yy * img.Width + xx) * img.Channels;
            o.Add((d, img.Data[at..(at + img.Channels)]));
        }
        return o;
    }

    private static List<List<(int D, byte[] V)>> FillProfiles(List<List<(int D, byte[] V)>> list, int ch)
    {
        var ok = Enumerable.Range(0, list.Count).Where(i => list[i].Count > 0).ToList();
        return list.Select((p, i) =>
        {
            if (p.Count > 0) return p;
            if (ok.Count == 0) return [(0, Enumerable.Repeat((byte)70, ch).ToArray()), (1, Enumerable.Repeat((byte)70, ch).ToArray())];
            int best = ok[0];
            foreach (var j in ok) if (Math.Abs(j - i) < Math.Abs(best - i)) best = j;
            return list[best];
        }).ToList();
    }

    private static int MinD(List<(int D, byte[] V)> p) => p.Aggregate(0, (m, x) => Math.Min(m, x.D));
    private static int MaxD(List<(int D, byte[] V)> p) => p.Aggregate(0, (m, x) => Math.Max(m, x.D));

    /// <summary>
    /// Image de base de la page ajoutée : l'image du modèle avec, dans chaque tableau vidé, papier propre et filets retracés aux
    /// positions actuelles (colonnes ajoutées ou retirées comprises), recopiés de l'imprimé ; en-têtes gardés, sauf les cases dont
    /// la colonne a changé. Numéros de page et dessous du dernier tableau effacés.
    /// </summary>
    public static Raster BlankBase(Raster img, Page page)
    {
        var A = page.Added;
        if (A is null) return img;
        int w = img.Width, h = img.Height, ch = img.Channels;
        var data = (byte[])img.Data.Clone();
        void Set(int x, int y, byte[] v)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * ch;
            for (int c = 0; c < ch; c++) data[o + c] = v[c];
        }
        void Fill(int x0, int y0, int x1, int y1, byte[] v)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int o = (y * w + x) * ch; for (int c = 0; c < ch; c++) data[o + c] = v[c]; }
        }
        for (int ti = 0; ti < A.Tables.Count; ti++)
        {
            var info = A.Tables[ti];
            var t = ti < page.Tables.Count ? page.Tables[ti] : null;
            if (info is null || t is null || t.ColumnXs.Count < 2) continue;
            var cols = t.ColumnXs.Select(R).ToList();
            var orig = info.Orig.Select(R).ToList();
            var ys = info.Ys.Select(R).ToList();
            int n = ys.Count - 1, head = Math.Min(info.Head, n);
            if (n < 1) continue;
            int top = ys[head], bottom = ys[n];
            int yRef = PageLayout.CleanRow(img, Math.Min(bottom, top + 4), Math.Max(1, bottom - top - 8), orig);
            var paper = PageLayout.PaperOf(img, yRef);
            int xRef = CleanCol(img, orig[0] + 5, orig[^1] - 5, top - 4, bottom + 4, ys.Skip(head), orig);
            var vOrig = FillProfiles(orig.Select(x => Profile(img, x, yRef, true)).ToList(), ch);
            var hp = FillProfiles(ys.Select(y => Profile(img, xRef, y, false)).ToList(), ch);
            var interior = Enumerable.Range(0, orig.Count).Where(i => i > 0 && i < orig.Count - 1).ToList();
            List<(int D, byte[] V)> VOf(int x)
            {
                int i = orig.IndexOf(x);
                if (i >= 0) return vOrig[i];
                var pool = interior.Count > 0 ? interior : Enumerable.Range(0, orig.Count).ToList();
                int best = pool[0];
                foreach (var j in pool) if (Math.Abs(orig[j] - x) < Math.Abs(orig[best] - x)) best = j;
                return vOrig[best];
            }
            var vp = cols.Select(VOf).ToList();
            int left = Math.Min(cols[0], orig[0]) - 4, right = Math.Max(cols[^1], orig[^1]) + 5;
            // 1. Corps du tableau : papier propre (sous un en-tête, son trait du bas est gardé tel qu'imprimé).
            if (head < n) Fill(left, head > 0 ? top + 4 : top - 3, right, bottom + 4, paper);
            // Trait retiré : son bout sous l'en-tête aussi.
            if (head > 0 && head < n) foreach (var x in orig) if (cols.IndexOf(x) < 0) Fill(x - 3, top + MaxD(hp[head]) + 1, x + 4, top + 4, paper);
            // 2. En-tête : les cases dont la colonne a changé sont vidées.
            for (int c = 0; c + 1 < cols.Count && head > 0; c++)
            {
                int i = orig.IndexOf(cols[c]);
                if (i >= 0 && i + 1 < orig.Count && orig[i + 1] == cols[c + 1]) continue;
                int x0 = cols[c] + MaxD(vp[c]) + 1, x1 = cols[c + 1] + MinD(vp[c + 1]);
                int mid = (cols[c] + cols[c + 1]) >> 1;
                for (int r = 0; r < head; r++)
                {
                    int t0 = 1, t1 = 1;
                    for (int d = 0; d <= 3; d++) if (mid < w && ys[r] + d < h && ys[r] + d >= 0 && PageLayout.Gray(img, mid, ys[r] + d) < Dark) t0 = d + 1;
                    for (int d = 0; d <= 3; d++) if (mid < w && ys[r + 1] - d >= 0 && ys[r + 1] - d < h && PageLayout.Gray(img, mid, ys[r + 1] - d) < Dark) t1 = d + 1;
                    Fill(x0, ys[r] + t0, x1, ys[r + 1] - t1 + 1, paper);
                }
            }
            // 3. Filets horizontaux du corps, d'un bord à l'autre du tableau.
            int xa = cols[0] + MinD(vp[0]), xb = cols[^1] + MaxD(vp[^1]);
            for (int r = head > 0 ? head + 1 : head; r <= n && head < n; r++)
                for (int x = xa; x <= xb; x++) foreach (var (d, v) in hp[r]) Set(x, ys[r] + d, v);
            // 4. Filets verticaux : dans le corps, et dans l'en-tête pour une colonne ajoutée.
            for (int c = 0; c < cols.Count; c++)
            {
                int x = cols[c];
                bool isNew = orig.IndexOf(x) < 0;
                int ya = isNew || head == 0 ? ys[0] + MinD(hp[0]) : top + MinD(hp[head]);
                int yb = bottom + MaxD(hp[n]);
                if (!isNew && head >= n) continue;
                for (int y = ya; y <= yb; y++) foreach (var (d, v) in vp[c]) Set(x + d, y, v);
            }
        }
        if (A.Tail is double tail)
        {
            int y0 = Math.Max(0, R(tail) + 5);
            if (y0 < h) Fill(0, y0, w, h, PageLayout.PaperOf(img, Math.Max(0, Math.Min(h - 1, R(tail) - 6))));
        }
        foreach (var z in A.Erase)
            Fill((int)Math.Floor(z.X), (int)Math.Floor(z.Y), (int)Math.Ceiling(z.X + z.Width), (int)Math.Ceiling(z.Y + z.Height),
                PageLayout.PaperOf(img, Math.Max(0, Math.Min(h - 1, (int)Math.Floor(z.Y) - 2))));
        return new Raster(w, h, ch, data);
    }
}
