namespace RetoucheInventaire.Core.Model;

/// <summary>Actions d'édition qui ne touchent jamais à la géométrie de la page.</summary>
public static class PageEdits
{
    /// <summary>
    /// Verrouille le texte hors tableau et tout champ dont le centre tombe dans une zone verrouillée
    /// (signature, tampon). Un champ verrouillé reprend son texte d'origine.
    /// </summary>
    public static void ApplyLocks(Page page)
    {
        foreach (var line in page.AllLines())
            foreach (var f in line.Cells)
            {
                bool inZone = page.LockZones.Any(z => z.Contains(f.Box.CenterX, f.Box.CenterY));
                f.Locked = line.OutsideTable || inZone;
                if (f.Locked && f.IsModified) f.Text = f.OriginalText;
            }
    }

    public static void AddLockZone(Page page, RectD zone)
    {
        if (zone.Width < 8 || zone.Height < 8) return;
        page.LockZones.Add(zone);
        ApplyLocks(page);
    }

    public static bool RemoveLockZoneAt(Page page, double x, double y)
    {
        int i = page.LockZones.FindLastIndex(z => z.Contains(x, y));
        if (i < 0) return false;
        page.LockZones.RemoveAt(i);
        ApplyLocks(page);
        return true;
    }

    private static readonly LineKind[] Cycle = [LineKind.Article, LineKind.Titre, LineKind.Total, LineKind.EnTete, LineKind.Ignoree];

    /// <summary>Passe au type de ligne suivant (choix de l'utilisateur, jamais écrasé ensuite).</summary>
    public static LineKind CycleKind(PrintedLine line)
    {
        int i = Array.IndexOf(Cycle, line.Kind);
        line.Kind = Cycle[(i + 1) % Cycle.Length];
        line.KindChosenByUser = true;
        return line.Kind;
    }

    public static string Label(LineKind kind) => kind switch
    {
        LineKind.Article => "Article",
        LineKind.Titre => "Titre",
        LineKind.Total => "Total",
        LineKind.EnTete => "En-tête",
        _ => "Non exportée",
    };

    public static (Page Page, PrintedLine Line)? FindLine(InventoryProject project, Field field)
    {
        foreach (var p in project.Pages)
            foreach (var l in p.AllLines())
                if (l.Cells.Contains(field)) return (p, l);
        return null;
    }
}
