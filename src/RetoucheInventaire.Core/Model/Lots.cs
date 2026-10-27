using System.Text;
namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Numéros de lot : un par objet (ligne d'article), dans l'ordre d'affichage, d'une page à l'autre.
/// Un numéro tapé ne change jamais les autres ; « Synchroniser automatiquement » remplit tout d'un coup.
/// 0 = objet absent : sa ligne passe après le dernier lot (voir <see cref="Export.Arrangement"/>).
/// La poubelle retire la case d'une ligne (ce n'est pas un objet) ; « + » en ajoute une.
/// </summary>
public static class Lots
{
    /// <summary>Ordre d'affichage des rangées d'un tableau (rangées déplacées à la main), sinon l'ordre imprimé.</summary>
    public static List<int> RowOrder(Table table)
    {
        int n = table.Rows.Count;
        var o = table.RowOrder;
        if (o is null || o.Count != n || o.Distinct().Count() != n || o.Any(r => r < 0 || r >= n)) return Enumerable.Range(0, n).ToList();
        return [.. o];
    }

    public static IEnumerable<Row> OrderedRows(Table table) => RowOrder(table).Select(r => table.Rows[r]);

    /// <summary>Lignes d'objet, dans l'ordre d'affichage (pages, tableaux, rangées déplacées à la main), sur les tableaux choisis.</summary>
    public static IEnumerable<(Page Page, Table Table, int Row, PrintedLine Line)> ObjectLines(InventoryProject project)
    {
        foreach (var page in project.Pages)
            foreach (var table in page.Tables)
            {
                if (table.LotsOff) continue;
                foreach (var r in RowOrder(table))
                {
                    var row = table.Rows[r];
                    var pick = PickOf(row);
                    foreach (var line in row.Lines)
                        if (IsObject(line, pick, page.Added != null)) yield return (page, table, r, line);
                }
            }
    }

    /// <summary>Objet d'une rangée : la règle, ou la 1re ligne d'une rangée ajoutée ou séparée (même vide : « Ajouter une ligne vide »).</summary>
    private static PrintedLine? PickOf(Row row) =>
        DefaultPick(row) ?? (row.Inserted != null ? row.Lines.FirstOrDefault(l => l.Kind == LineKind.Article && !l.NoLot && (l.Copy || l.FirstText.Length > 0)) : null);

    // ---------- Lots page par page, tableau par tableau ----------

    /// <summary>« II - STOCK », « [- MOBILIER » (lecture abîmée) : rubrique numérotée d'un récapitulatif.</summary>
    private static readonly System.Text.RegularExpressions.Regex RecapLike = new(@"^\W{0,2}[IVXLliJj1|!{\[]{1,5}\s*[-–.]\s+\S");

    /// <summary>
    /// Le tableau est un tableau d'objets (proposé pour les lots) : au moins deux colonnes de montants et des objets selon la
    /// règle ; pas un cadre de texte (en-tête de l'étude, signature), ni un récapitulatif (« I - MOBILIER… 148 760 »).
    /// </summary>
    /// <summary>Tableau d'objets (au moins trois colonnes dont une large, la désignation) : on peut y ajouter une case même si ses lots sont éteints.</summary>
    public static bool ItemTable(Table? t)
    {
        if (t is null || !t.HasGrid || t.RowYs.Count < 2 || t.ColumnXs.Count < 4) return false;
        double widest = 0;
        for (int c = 0; c + 1 < t.ColumnXs.Count; c++) widest = Math.Max(widest, t.ColumnXs[c + 1] - t.ColumnXs[c]);
        return widest * 100 >= 35 * (t.ColumnXs[^1] - t.ColumnXs[0]);
    }

    /// <summary>
    /// « + » sur une ligne (ex. un objet dans une grande case) : une case vide apparaît à sa hauteur, rien d'autre ne bouge.
    /// Dans un tableau qui n'avait pas de lots, le tableau les reçoit, mais seule cette ligne a une case.
    /// </summary>
    public static void AddHere(InventoryProject project, Table? table, PrintedLine line)
    {
        if (table is { LotsOff: true })
        {
            table.LotsOff = false;
            foreach (var o in ObjectLines(project).ToList())
                if (o.Table == table && o.Line != line && !HasCell(o.Line)) o.Line.NoLot = true;
        }
        Add(project, line);
    }

    public static bool TableHasObjects(Table table)
    {
        if (!table.HasGrid || table.RowYs.Count < 2 || table.ColumnXs.Count < 4) return false;
        // Une colonne large (la désignation) : pas un cadre de titre sur fond tramé, lu comme une suite de colonnes étroites.
        if (!ItemTable(table)) return false;
        var withValues = table.Rows.SelectMany(r => r.Lines).Where(l => l.Kind != LineKind.EnTete && l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text))).ToList();
        if (withValues.Count > 0 && withValues.Count(l => RecapLike.IsMatch(l.FirstText)) * 2 >= withValues.Count) return false;
        return table.Rows.Any(row => { var pick = PickOf(row); return row.Lines.Any(l => IsObject(l, pick)); });
    }

    public sealed record LotTable(Table Table, int Index, string Name, bool Objects);

    /// <summary>Tableaux d'une page qui peuvent recevoir des lots (avec leur nom : 1re ligne d'en-tête ou de texte).</summary>
    public static List<LotTable> LotTables(Page page) =>
        page.Tables.Select((t, i) => (t, i))
            .Where(x => x.t.Rows.Any(r => r.Lines.Any(l => l.FirstText.Length > 0)))
            .Select(x =>
            {
                // Nom : la 1re ligne d'objet (bien lue), sinon l'en-tête ou la 1re ligne de texte.
                var lines = x.t.Rows.SelectMany(r => r.Lines).ToList();
                var head = x.t.Rows.Select(PickOf).FirstOrDefault(l => l != null && l.FirstText.Length > 0)
                    ?? lines.FirstOrDefault(l => l.Kind == LineKind.EnTete && l.FirstText.Length > 0) ?? lines.FirstOrDefault(l => l.FirstText.Length > 0);
                return new LotTable(x.t, x.i, head?.FirstText ?? $"Tableau {x.i + 1}", TableHasObjects(x.t));
            }).ToList();

    /// <summary>Choix proposé : les tableaux qui contiennent des objets (déjà choisis si les lots existent).</summary>
    public static bool DefaultChoice(InventoryProject project, Table table) => IsActive(project) ? !table.LotsOff : TableHasObjects(table);

    /// <summary>Applique le choix (tableaux cochés) : les autres n'ont plus de case ni de numéro ; rien d'autre ne change.</summary>
    public static void SetTables(InventoryProject project, ISet<Table> chosen)
    {
        foreach (var page in project.Pages) foreach (var t in page.Tables) t.LotsOff = !chosen.Contains(t);
        Resync(project);
    }

    /// <summary>
    /// L'objet de la rangée selon la règle : la ligne d'article qui porte les valeurs, sinon la première
    /// (les suivantes sont la suite du même objet ; pas les récapitulatifs « II - STOCK »). Null si la rangée n'a pas d'article.
    /// </summary>
    private static PrintedLine? DefaultPick(Row row)
    {
        // Rangée d'en-tête ou de total (souvent sur fond grisé, où la lecture invente des lignes) : jamais d'objet.
        if (row.Lines.Any(l => l.Kind == LineKind.EnTete || Analysis.LineClassifier.LooksLikeTotal(l))) return null;
        var articles = row.Lines.Where(l => l.Kind == LineKind.Article && !l.OutsideTable && !Analysis.LineClassifier.IsRomanHeading(l.FirstText)).ToList();
        if (articles.Count == 0) return null;
        var line = articles.FirstOrDefault(l => l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text))) ?? articles[0];
        if (line.Cells.All(c => string.IsNullOrWhiteSpace(c.Text))) return null;
        // Lecture parasite (fond grisé, traits lus comme du texte) : ni montant lisible, ni début d'objet (« 1 … », « +/- 20 … »).
        if (!row.Lines.Any(l => l.Cells.Skip(1).Any(c => AmountLike.IsMatch(c.Text))) && !ItemStartPick.IsMatch(line.FirstText) && !PlainText(line.FirstText)) return null;
        return line;
    }

    /// <summary>Montant (« 25 000 », « 1 500,50 », « mémoire ») : au moins un chiffre, rien d'autre que des chiffres et séparateurs.</summary>
    private static readonly System.Text.RegularExpressions.Regex AmountLike = new(@"^\s*(?:m[ée]moire|[+-]?\d[\d\s\u00A0\u202F.,]*)\s*€?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ItemStartPick = new(@"^\s*(\+/-\s*)?\d{1,3}\s+\p{L}");
    private static readonly System.Text.RegularExpressions.Regex PlainWord = new(@"^\p{L}{3,}[\p{L}'’.,:-]*$");

    /// <summary>Texte lisible : au moins deux mots de trois lettres ou plus, et plus de lettres que de signes.</summary>
    private static bool PlainText(string t)
    {
        int words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => PlainWord.IsMatch(w));
        var runes = t.EnumerateRunes().ToList();
        int letters = runes.Count(Rune.IsLetter), signs = runes.Count(r => !Rune.IsLetter(r) && !Rune.IsDigit(r) && !Rune.IsWhiteSpace(r));
        return words >= 2 && letters > 3 * signs;
    }

    // ForceLot : « + » sur une ligne écrite ; LotHere : « + » sur une ligne encore vide (la case est là avant le texte).
    // Page ajoutée (added) : pas de case toute seule, seulement avec « + » (on peut n'y écrire qu'un titre).
    private static bool IsObject(PrintedLine line, PrintedLine? pick, bool added = false) =>
        !line.NoLot && !line.OutsideTable && !line.Deleted && ((line == pick && !added) || line.LotHere || (line.ForceLot && line.FirstText.Length > 0));

    /// <summary>
    /// Ligne sans numéro à laquelle on peut en ajouter un (« + ») : article ou titre (pas un total, un en-tête, un récapitulatif),
    /// qui commence sa rangée ou ressemble à un objet (« 1 gerbeur… »).
    /// </summary>
    public static bool CanAdd(PrintedLine line, Row? row = null)
    {
        if (HasCell(line) || line.OutsideTable || line.Kind is not (LineKind.Article or LineKind.Titre)) return false;
        var t = line.FirstText;
        // Ligne encore vide d'une rangée ajoutée ou d'une page ajoutée : « + » pour y mettre une case avant d'écrire.
        if (t.Length == 0) return line.Kind == LineKind.Article && (row?.Inserted != null || line.AutoLot);
        if (Analysis.LineClassifier.IsRomanHeading(t)) return false;
        return row is null || row.Lines.FirstOrDefault(l => l.FirstText.Length > 0) == line || Analysis.LineClassifier.LooksLikeItem(t);
    }

    /// <summary>Lot 0 : objet absent ou barré, déplacé après le dernier lot.</summary>
    public static bool IsZero(PrintedLine line) => int.TryParse(line.Lot.Trim(), out var v) && v == 0;

    /// <summary>La colonne des lots existe (au moins une case, même vide).</summary>
    public static bool IsActive(InventoryProject project) =>
        project.Pages.SelectMany(p => p.AllLines()).Any(HasCell);

    /// <summary>Case de lot à afficher sur cette ligne (numérotée ou vide).</summary>
    public static bool HasCell(PrintedLine l) => l.Lot.Length > 0 || l.LotSlot;

    /// <summary>Bouton « Lots » : une case vide par objet ; on tape les numéros soi-même, rien ne se remplit tout seul.</summary>
    public static void CreateEmpty(InventoryProject project)
    {
        Clear(project);
        project.LotsByHand = true;
        foreach (var (_, _, _, line) in ObjectLines(project).ToList()) line.LotSlot = true;
    }

    /// <summary>
    /// « Synchroniser » : remplit les cases vides, sans toucher aux numéros déjà là (tapés ou remplis avant), dans l'ordre affiché.
    /// Une case vide prend le numéro qui suit le lot précédent (1 au début). Si les lots suivants se suivaient à partir de ce numéro
    /// (6, 7, 8…), ils sont décalés d'un cran (7, 8, 9…) pour éviter un doublon ; un ordre fait à la main (30, 29, 6…) n'est pas touché.
    /// <paramref name="pages"/> : seulement les cases vides de ces pages (les numéros des autres pages servent de repère).
    /// </summary>
    public static void FillAutomatically(InventoryProject project, ICollection<Page>? pages = null)
    {
        Resync(project);
        var list = ObjectLines(project).ToList();
        static int? Num(PrintedLine l) => System.Text.RegularExpressions.Regex.IsMatch(l.Lot.Trim(), @"^[+-]?\d+$") ? int.Parse(l.Lot.Trim()) : null;
        int prev = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var (page, _, _, line) = list[i];
            if (line.Lot.Length > 0) { if (Num(line) is int v0 && v0 != 0) prev = v0; continue; }
            if (pages != null && !pages.Contains(page)) continue;
            int v = prev + 1;
            line.Lot = v.ToString();
            line.LotManual = false;
            // Décaler la suite : les lots qui se suivaient à partir de v prennent un cran de plus (les 0 et les cases vides sont passés).
            int k = v;
            for (int j = i + 1; j < list.Count; j++)
            {
                var o = list[j].Line;
                if (o.Lot.Length == 0 || IsZero(o)) continue;
                if (Num(o) != k) break;
                o.Lot = (k + 1).ToString();
                k++;
            }
            prev = v;
        }
    }

    /// <summary>Renumérote tout à partir de <paramref name="start"/> (les numéros tapés sont oubliés, sauf les 0).</summary>
    public static void Number(InventoryProject project, int start = 1)
    {
        Resync(project);
        if (!IsActive(project)) foreach (var (_, _, _, line) in ObjectLines(project).ToList()) line.LotSlot = true;
        foreach (var l in project.Pages.SelectMany(p => p.AllLines()).Where(l => !IsZero(l))) { l.Lot = ""; l.LotManual = false; }
        var first = ObjectLines(project).FirstOrDefault(o => !IsZero(o.Line));
        if (first.Line is null) return;
        first.Line.Lot = start.ToString();
        first.Line.LotManual = true;
        FillAutomatically(project);
    }

    /// <summary>Vide toutes les cases (la colonne reste).</summary>
    public static void Empty(InventoryProject project)
    {
        foreach (var l in project.Pages.SelectMany(p => p.AllLines())) { l.Lot = ""; l.LotManual = false; }
        Resync(project);
    }

    public static void Clear(InventoryProject project)
    {
        foreach (var l in project.Pages.SelectMany(p => p.AllLines())) { l.Lot = ""; l.LotManual = false; l.LotSlot = false; }
        project.LotsByHand = false;
    }

    /// <summary>Numéro tapé à la main : seule cette case change.</summary>
    public static void Set(InventoryProject project, PrintedLine line, string value)
    {
        line.Lot = value.Trim();
        line.LotManual = line.Lot.Length > 0;
        if (!IsZero(line)) line.EndAt = null; // remis dans la vente : l'endroit choisi pour son lot 0 est oublié
        Resync(project);
    }

    /// <summary>Poubelle : cette ligne n'est pas un objet ; sa case disparaît (les autres numéros ne bougent pas).</summary>
    public static void Remove(InventoryProject project, PrintedLine line)
    {
        line.NoLot = true;
        line.ForceLot = false;
        line.LotHere = false;
        line.Lot = "";
        line.LotManual = false;
        Resync(project);
    }

    /// <summary>« + » : cette ligne est un objet ; une case vide apparaît.</summary>
    public static void Add(InventoryProject project, PrintedLine line)
    {
        line.NoLot = false;
        line.ForceLot = true;
        if (line.FirstText.Length == 0) line.LotHere = true; // ligne encore vide : la case reste en attendant le texte
        line.Lot = "";
        line.LotManual = false;
        if (!IsActive(project)) line.LotSlot = true;
        Resync(project);
    }

    /// <summary>Met les cases en place (une par objet) sans toucher à aucun numéro.</summary>
    public static void Resync(InventoryProject project)
    {
        if (!IsActive(project)) return;
        var objects = ObjectLines(project).Select(o => o.Line).ToHashSet();
        foreach (var l in project.Pages.SelectMany(p => p.AllLines()))
        {
            l.LotSlot = objects.Contains(l);
            if (!l.LotSlot && !l.Deleted) { l.Lot = ""; l.LotManual = false; } // ligne supprimée : son numéro est gardé (si on la remet)
        }
    }

    // ---------- Emplacement : une colonne ajoutée à gauche de chaque tableau, dans la marge ----------

    /// <summary>Bord gauche du tableau (son filet vertical gauche s'il a une grille).</summary>
    public static double TableLeft(Table table) =>
        table.HasGrid && table.ColumnXs.Count > 0 ? table.ColumnXs[0] : table.Bounds.Left;

    /// <summary>
    /// Largeur de la colonne des lots sur cette page : de quoi écrire 3 chiffres (plus si besoin)
    /// dans la taille du texte imprimé, sans dépasser la marge gauche.
    /// </summary>
    public static double ColumnWidth(Page page)
    {
        var sizes = page.Tables.SelectMany(t => t.Rows).SelectMany(r => r.Lines)
            .Select(l => l.Cells.FirstOrDefault()?.FontSize ?? 0).Where(s => s > 0).OrderBy(s => s).ToList();
        double fs = sizes.Count > 0 ? sizes[sizes.Count / 2] : 16;
        int digits = Math.Max(3, page.AllLines().Select(l => l.Lot.Length).DefaultIfEmpty(0).Max());
        double desired = Math.Floor(fs * (0.55 * digits + 0.9) + 0.5);
        var lefts = page.Tables.Where(t => t.Rows.SelectMany(r => r.Lines).Any(HasCell)).Select(TableLeft).ToList();
        double avail = lefts.Count > 0 ? lefts.Min() - 6 : desired;
        return Math.Max(8, Math.Min(desired, avail));
    }

    /// <summary>
    /// Ligne d'une grande case qui a sa propre case de lot (pas la 1re ligne écrite de la rangée, ajoutée avec « + » ou
    /// avec d'autres cases dans la rangée) : une petite case fermée à sa hauteur, pas toute la grande case.
    /// </summary>
    public static bool OwnBox(Row row, PrintedLine line)
    {
        if (!HasCell(line)) return false;
        var first = row.Lines.FirstOrDefault(l => HasCell(l) || l.Cells.Any(c => !string.IsNullOrWhiteSpace(c.Text)));
        if (first is null || first == line) return false;
        return line.ForceLot || line.LotHere || row.Lines.Count(HasCell) >= 2;
    }

    /// <summary>Hauteur de la case de lot de <paramref name="line"/> dans la rangée r : sa petite case, ou la rangée (jusqu'à la 1re petite case).</summary>
    public static (double Top, double Bottom, bool Own) LotSegment(Table table, int r, PrintedLine line)
    {
        var row = table.Rows[r];
        int i = row.Lines.IndexOf(line);
        double top = table.RowYs[r], bottom = table.RowYs[r + 1];
        if (OwnBox(row, line))
        {
            var prev = i > 0 ? row.Lines[i - 1] : null;
            var next = i + 1 < row.Lines.Count ? row.Lines[i + 1] : null;
            return (prev != null ? (prev.Bounds.Bottom + line.Bounds.Y) / 2 : top, next != null ? (line.Bounds.Bottom + next.Bounds.Y) / 2 : bottom, true);
        }
        var own = row.Lines.Where((l, k) => k > i && OwnBox(row, l)).FirstOrDefault();
        return (top, own != null ? LotSegment(table, r, own).Top : bottom, false);
    }

    /// <summary>
    /// Case du numéro de lot : dans la colonne, à la hauteur de la ligne d'objet (en haut, réglage par défaut), ou au milieu
    /// ou en bas de sa case (clic droit : PrintedLine.LotAlign).
    /// </summary>
    public static RectD NumberBox(Table table, PrintedLine line, double columnWidth)
    {
        var b = line.Bounds;
        b = b with { Height = b.Height - line.Extra }; // à la hauteur de la 1re ligne de texte, même si la case a grandi
        double right = TableLeft(table), left = Math.Max(0, right - columnWidth);
        double h = Math.Max(8, b.Height);
        double cy = b.CenterY;
        int r = line.LotAlign is "Middle" or "Bottom" ? table.Rows.FindIndex(x => x.Lines.Contains(line)) : -1;
        if (r >= 0 && r + 1 < table.RowYs.Count)
        {
            var s = LotSegment(table, r, line);
            cy = line.LotAlign == "Middle" ? (s.Top + s.Bottom) / 2 : s.Bottom - (cy - s.Top);
            if (s.Bottom - s.Top >= h) cy = Math.Min(s.Bottom - h / 2, Math.Max(s.Top + h / 2, cy));
        }
        return new RectD(left + 2, cy - h / 2, Math.Max(4, right - left - 4), h);
    }

    /// <summary>Numéros de lot d'une page, avec la case où les écrire.</summary>
    public static List<(PrintedLine Line, RectD Box)> Labels(Page page)
    {
        double w = ColumnWidth(page);
        return page.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Lines)
                .Where(HasCell)
                .Select(l => (l, NumberBox(t, l, w))))
            .ToList();
    }

    // ---------- Document déjà numéroté (numéros imprimés dans la marge, ex. PDF exporté puis réimporté) ----------

    /// <summary>
    /// Bande de la marge gauche d'un tableau à lots, là où un numéro peut déjà être imprimé : à lire (OCR) quand on crée
    /// les cases, pour ne pas effacer les numéros du document. null si pas de marge.
    /// </summary>
    public static RectD? PrintedLotZone(Page page, Table table)
    {
        var solo = new InventoryProject { Pages = [page] };
        if (!ObjectLines(solo).Any(o => o.Table == table)) return null;
        double w = ColumnWidth(page);
        int right = (int)Math.Floor(TableLeft(table)) - 2, left = Math.Max(0, (int)Math.Floor(TableLeft(table) - 1.8 * w));
        if (right - left < 10) return null;
        int top = Math.Max(0, (int)Math.Floor(table.RowYs[0]) - 4), bottom = (int)Math.Ceiling(table.RowYs[^1]) + 4;
        return new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>Texte lu qui ressemble à un numéro de lot : chiffres (« 83 », « 0 »), ou un court code en lettres (« VGG »).</summary>
    public static string? LotWord(string? text)
    {
        string t = System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"[|\][(){}.,:;'""`_~-]", "");
        if (t.Any(char.IsDigit))
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, "[Oo]", "0");
            t = System.Text.RegularExpressions.Regex.Replace(t, "[lI!]", "1");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[Ss](?=\d|$)", "5");
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,5}$")) return int.Parse(t).ToString();
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,4}[A-Za-z]{0,3}$") || System.Text.RegularExpressions.Regex.IsMatch(t, "^[A-Z]{2,4}$")) return t;
        return null;
    }

    /// <summary>
    /// Numéros déjà imprimés dans la marge (mots lus, en coordonnées de la page) : chaque case vide prend celui qui est à
    /// sa hauteur, comme s'il avait été tapé. Les cases déjà remplies ne changent pas. Renvoie le nombre de numéros repris.
    /// </summary>
    public static int ApplyPrintedLots(InventoryProject project, Page page, Table table, IEnumerable<(string Text, RectD Box)> words, bool digitsOnly = false)
    {
        double w = ColumnWidth(page);
        var cands = words.Select(x => (Lot: LotWord(x.Text), Cy: x.Box.Y + x.Box.Height / 2, H: x.Box.Height))
            .Where(c => c.Lot != null && (!digitsOnly || c.Lot.All(char.IsDigit))).ToList();
        var used = new HashSet<int>();
        int n = 0;
        foreach (var o in ObjectLines(new InventoryProject { Pages = [page] }).ToList())
        {
            if (o.Table != table || o.Line.Lot.Length > 0) continue;
            var b = NumberBox(table, o.Line, w);
            double cy = b.Y + b.Height / 2;
            int best = -1;
            for (int i = 0; i < cands.Count; i++)
            {
                if (used.Contains(i)) continue;
                double d = Math.Abs(cands[i].Cy - cy);
                if (d <= Math.Max(b.Height, cands[i].H) * 0.6 && (best < 0 || d < Math.Abs(cands[best].Cy - cy))) best = i;
            }
            if (best < 0) continue;
            used.Add(best);
            o.Line.Lot = cands[best].Lot!;
            o.Line.LotManual = true;
            n++;
        }
        if (n > 0) Resync(project);
        return n;
    }

    /// <summary>
    /// Poignée ⋮⋮ (Modifier) quand les lignes sont triées ou déplacées : la ligne est posée à la main avant la rangée
    /// <paramref name="before"/> du tableau (null : à la fin) — elle y reste, même triée (PrintedLine.MoveTo).
    /// </summary>
    public static void MoveToRow(Page page, Table table, List<PrintedLine> lines, int? before, int skip = -1)
    {
        var order = RowOrder(table).Where(r => r != skip).ToList();
        int i = before is int b ? order.IndexOf(b) : order.Count;
        PrintedLine? anchor = null;
        for (int k = i - 1; k >= 0 && anchor is null; k--) anchor = table.Rows[order[k]].Lines.FirstOrDefault(l => l.Cells.Count > 0 && !string.IsNullOrEmpty(l.Cells[0].Id) && !lines.Contains(l));
        foreach (var l in lines)
            l.MoveTo = anchor != null ? new LotMove { Page = page.Id, Where = "after", Anchor = anchor.Cells[0].Id } : new LotMove { Page = page.Id, Where = "start", Anchor = "" };
    }
}
