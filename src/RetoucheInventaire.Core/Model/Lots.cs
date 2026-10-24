namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Numéros de lot : un par objet (ligne d'article), continus d'une page à l'autre.
/// Un numéro tapé à la main fait repartir la suite de lui (80 → 81 : le suivant devient 82…).
/// La poubelle retire le numéro d'une ligne (ce n'est pas un objet) ; « + » en ajoute un.
/// </summary>
public static class Lots
{
    /// <summary>Lignes d'objet, dans l'ordre du document.</summary>
    public static IEnumerable<(Page Page, Table Table, PrintedLine Line)> ObjectLines(InventoryProject project)
    {
        foreach (var page in project.Pages)
            foreach (var table in page.Tables)
                foreach (var row in table.Rows)
                {
                    var pick = DefaultPick(row);
                    foreach (var line in row.Lines)
                        if (IsObject(line, pick)) yield return (page, table, line);
                }
    }

    /// <summary>
    /// L'objet de la rangée selon la règle : la ligne d'article qui porte les valeurs, sinon la première
    /// (les suivantes sont la suite du même objet). Null si la rangée n'a pas d'article.
    /// </summary>
    private static PrintedLine? DefaultPick(Row row)
    {
        var articles = row.Lines.Where(l => l.Kind == LineKind.Article && !l.OutsideTable).ToList();
        if (articles.Count == 0) return null;
        var line = articles.FirstOrDefault(l => l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text))) ?? articles[0];
        return line.Cells.All(c => string.IsNullOrWhiteSpace(c.Text)) ? null : line;
    }

    private static bool IsObject(PrintedLine line, PrintedLine? pick) =>
        !line.NoLot && !line.OutsideTable && (line == pick || (line.ForceLot && line.FirstText.Length > 0));

    /// <summary>Ligne sans numéro à laquelle on peut en ajouter un (« + »).</summary>
    public static bool CanAdd(PrintedLine line) =>
        line.Lot.Length == 0 && !line.OutsideTable && line.Kind is not (LineKind.EnTete or LineKind.Ignoree) && line.FirstText.Length > 0;

    public static bool IsActive(InventoryProject project) =>
        project.Pages.SelectMany(p => p.AllLines()).Any(l => l.Lot.Length > 0);

    /// <summary>Numérote tous les objets à partir de <paramref name="start"/> (les lignes retirées à la poubelle le restent).</summary>
    public static void Number(InventoryProject project, int start = 1)
    {
        foreach (var l in project.Pages.SelectMany(p => p.AllLines())) { l.Lot = ""; l.LotManual = false; }
        var first = ObjectLines(project).FirstOrDefault();
        if (first.Line is null) return;
        first.Line.Lot = start.ToString();
        first.Line.LotManual = true;
        Resync(project);
    }

    public static void Clear(InventoryProject project)
    {
        foreach (var l in project.Pages.SelectMany(p => p.AllLines())) { l.Lot = ""; l.LotManual = false; }
    }

    /// <summary>Numéro tapé à la main sur une ligne : la suite se recale automatiquement.</summary>
    public static void Set(InventoryProject project, PrintedLine line, string value)
    {
        line.Lot = value.Trim();
        line.LotManual = line.Lot.Length > 0;
        Resync(project);
    }

    /// <summary>Poubelle : cette ligne n'est pas un objet ; les numéros suivants se décalent.</summary>
    public static void Remove(InventoryProject project, PrintedLine line)
    {
        line.NoLot = true;
        line.ForceLot = false;
        line.Lot = "";
        line.LotManual = false;
        Resync(project);
    }

    /// <summary>« + » : cette ligne est un objet ; elle prend le numéro qui suit le précédent.</summary>
    public static void Add(InventoryProject project, PrintedLine line)
    {
        line.NoLot = false;
        line.ForceLot = true;
        line.Lot = "";
        line.LotManual = false;
        bool active = IsActive(project);
        if (!active) { line.Lot = "1"; }
        Resync(project);
    }

    /// <summary>
    /// Recalcule les numéros (bouton « Synchroniser », et après chaque lot tapé, type de ligne changé,
    /// page ajoutée ou retirée). Les numéros tapés à la main sont gardés ; les autres suivent le précédent.
    /// </summary>
    public static void Resync(InventoryProject project)
    {
        if (!IsActive(project)) return;
        var objects = ObjectLines(project).Select(o => o.Line).ToHashSet();
        foreach (var l in project.Pages.SelectMany(p => p.AllLines()).Where(l => !objects.Contains(l))) { l.Lot = ""; l.LotManual = false; }
        int next = 1;
        foreach (var (_, _, line) in ObjectLines(project))
        {
            if (line.LotManual && int.TryParse(line.Lot, out var v)) next = v + 1;
            else if (line.LotManual && line.Lot.Length > 0) { /* « 12 bis » : gardé tel quel */ }
            else { line.Lot = next.ToString(); line.LotManual = false; next++; }
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
        var lefts = page.Tables.Where(t => t.Rows.SelectMany(r => r.Lines).Any(l => l.Lot.Length > 0)).Select(TableLeft).ToList();
        double avail = lefts.Count > 0 ? lefts.Min() - 6 : desired;
        return Math.Max(8, Math.Min(desired, avail));
    }

    /// <summary>Case du numéro de lot : dans la colonne, à la hauteur de la ligne d'objet.</summary>
    public static RectD NumberBox(Table table, PrintedLine line, double columnWidth)
    {
        var b = line.Bounds;
        double right = TableLeft(table), left = Math.Max(0, right - columnWidth);
        double h = Math.Max(8, b.Height);
        return new RectD(left + 2, b.CenterY - h / 2, Math.Max(4, right - left - 4), h);
    }

    /// <summary>Numéros de lot d'une page, avec la case où les écrire.</summary>
    public static List<(PrintedLine Line, RectD Box)> Labels(Page page)
    {
        double w = ColumnWidth(page);
        return page.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Lines)
                .Where(l => l.Lot.Length > 0)
                .Select(l => (l, NumberBox(t, l, w))))
            .ToList();
    }
}
