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

    /// <summary>Lignes d'objet, dans l'ordre d'affichage (pages, tableaux, rangées déplacées à la main).</summary>
    public static IEnumerable<(Page Page, Table Table, int Row, PrintedLine Line)> ObjectLines(InventoryProject project)
    {
        foreach (var page in project.Pages)
            foreach (var table in page.Tables)
                foreach (var r in RowOrder(table))
                {
                    var row = table.Rows[r];
                    var pick = DefaultPick(row);
                    foreach (var line in row.Lines)
                        if (IsObject(line, pick)) yield return (page, table, r, line);
                }
    }

    /// <summary>
    /// L'objet de la rangée selon la règle : la ligne d'article qui porte les valeurs, sinon la première
    /// (les suivantes sont la suite du même objet ; pas les récapitulatifs « II - STOCK »). Null si la rangée n'a pas d'article.
    /// </summary>
    private static PrintedLine? DefaultPick(Row row)
    {
        var articles = row.Lines.Where(l => l.Kind == LineKind.Article && !l.OutsideTable && !Analysis.LineClassifier.IsRomanHeading(l.FirstText)).ToList();
        if (articles.Count == 0) return null;
        var line = articles.FirstOrDefault(l => l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text))) ?? articles[0];
        return line.Cells.All(c => string.IsNullOrWhiteSpace(c.Text)) ? null : line;
    }

    private static bool IsObject(PrintedLine line, PrintedLine? pick) =>
        !line.NoLot && !line.OutsideTable && (line == pick || (line.ForceLot && line.FirstText.Length > 0));

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
    /// « Synchroniser automatiquement » : remplit toutes les cases d'un coup, dans l'ordre d'affichage.
    /// Les numéros tapés à la main restent et la suite repart d'eux ; 0 ne prend pas de numéro.
    /// </summary>
    public static void FillAutomatically(InventoryProject project)
    {
        Resync(project);
        int next = 1;
        foreach (var (_, _, _, line) in ObjectLines(project))
        {
            if (line.LotManual && int.TryParse(line.Lot, out var v))
            {
                if (v != 0) next = v + 1;
            }
            else if (line.LotManual && line.Lot.Length > 0) { /* « 12 bis » : gardé tel quel */ }
            else { line.Lot = next.ToString(); line.LotManual = false; next++; }
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
            if (!l.LotSlot) { l.Lot = ""; l.LotManual = false; }
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
