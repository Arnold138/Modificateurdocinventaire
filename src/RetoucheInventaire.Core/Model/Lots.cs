namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Numéros de lot : un par objet (ligne d'article), continus d'une page à l'autre.
/// Un numéro tapé à la main fait repartir la suite de lui (80 → 81 : le suivant devient 82…).
/// </summary>
public static class Lots
{
    /// <summary>Lignes d'objet, dans l'ordre du document : la première ligne d'article de chaque rangée.</summary>
    public static IEnumerable<(Page Page, Table Table, PrintedLine Line)> ObjectLines(InventoryProject project)
    {
        foreach (var page in project.Pages)
            foreach (var table in page.Tables)
                foreach (var row in table.Rows)
                {
                    var articles = row.Lines.Where(l => l.Kind == LineKind.Article && !l.OutsideTable).ToList();
                    if (articles.Count == 0) continue;
                    // La ligne qui porte les valeurs ; sinon la première (les suivantes sont la suite du même objet).
                    var line = articles.FirstOrDefault(l => l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text))) ?? articles[0];
                    if (line.Cells.All(c => string.IsNullOrWhiteSpace(c.Text))) continue;
                    yield return (page, table, line);
                }
    }

    public static bool IsActive(InventoryProject project) =>
        project.Pages.SelectMany(p => p.AllLines()).Any(l => l.Lot.Length > 0);

    /// <summary>Numérote tous les objets à partir de <paramref name="start"/>.</summary>
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
        if (line.Lot.Length == 0) line.LotManual = false;
        Resync(project);
    }

    /// <summary>
    /// Recalcule les numéros après une modification (lot tapé, type de ligne changé, page ajoutée ou retirée).
    /// Les numéros tapés à la main sont gardés ; les autres suivent le précédent.
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

    /// <summary>
    /// Emplacement du numéro de lot dans la marge gauche de la page (pour l'image exportée),
    /// à la hauteur de la ligne, juste avant le bord du tableau.
    /// </summary>
    public static RectD LabelBox(Table table, PrintedLine line)
    {
        var b = line.Bounds;
        double w = Math.Clamp(table.Bounds.Left - 6, 14, 70);
        double x = Math.Max(1, table.Bounds.Left - 4 - w);
        double h = Math.Max(8, b.Height);
        return new RectD(x, b.CenterY - h / 2, w, h);
    }

    /// <summary>Numéros de lot d'une page, avec leur emplacement dans la marge.</summary>
    public static List<(PrintedLine Line, RectD Box)> Labels(Page page) =>
        page.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Lines)
                .Where(l => l.Lot.Length > 0)
                .Select(l => (l, LabelBox(t, l))))
            .ToList();
}
