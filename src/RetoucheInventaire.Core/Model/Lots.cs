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
                        if (IsObject(line, pick)) yield return (page, table, r, line);
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
    public static bool TableHasObjects(Table table)
    {
        if (!table.HasGrid || table.RowYs.Count < 2 || table.ColumnXs.Count < 4) return false;
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

    private static bool IsObject(PrintedLine line, PrintedLine? pick) =>
        !line.NoLot && !line.OutsideTable && !line.Deleted && (line == pick || (line.ForceLot && line.FirstText.Length > 0));

    /// <summary>
    /// Ligne sans numéro à laquelle on peut en ajouter un (« + ») : article ou titre (pas un total, un en-tête, un récapitulatif),
    /// qui commence sa rangée ou ressemble à un objet (« 1 gerbeur… »).
    /// </summary>
    public static bool CanAdd(PrintedLine line, Row? row = null)
    {
        if (HasCell(line) || line.OutsideTable || line.Kind is not (LineKind.Article or LineKind.Titre)) return false;
        var t = line.FirstText;
        if (t.Length == 0 || Analysis.LineClassifier.IsRomanHeading(t)) return false;
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
        line.Lot = "";
        line.LotManual = false;
        Resync(project);
    }

    /// <summary>« + » : cette ligne est un objet ; une case vide apparaît.</summary>
    public static void Add(InventoryProject project, PrintedLine line)
    {
        line.NoLot = false;
        line.ForceLot = true;
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

    /// <summary>Case du numéro de lot : dans la colonne, à la hauteur de la ligne d'objet.</summary>
    public static RectD NumberBox(Table table, PrintedLine line, double columnWidth)
    {
        var b = line.Bounds;
        b = b with { Height = b.Height - line.Extra }; // à la hauteur de la 1re ligne de texte, même si la case a grandi
        double right = TableLeft(table), left = Math.Max(0, right - columnWidth);
        double h = Math.Max(8, b.Height);
        return new RectD(left + 2, b.CenterY - h / 2, Math.Max(4, right - left - 4), h);
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
}
