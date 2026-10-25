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

    private static string KeyOf(string? s) =>
        string.Join(' ', (s ?? "").Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Après un recadrage (le texte est relu) : chaque ligne relue reprend, depuis la ligne d'avant de même texte (dans l'ordre),
    /// son numéro de lot, sa case de lot, son type choisi à la main, et les corrections de ses cases (texte, traits, couleurs,
    /// total tapé). Choix des tableaux pour les lots repris dans l'ordre des tableaux. Renvoie le nombre de lignes retrouvées.
    /// </summary>
    public static int CarryOver(Page old, Page fresh)
    {
        var pool = new Dictionary<string, Queue<PrintedLine>>();
        foreach (var l in old.AllLines())
        {
            string k = KeyOf(l.Cells.FirstOrDefault()?.OriginalText);
            if (k.Length == 0) continue;
            if (!pool.TryGetValue(k, out var q)) pool[k] = q = new Queue<PrintedLine>();
            q.Enqueue(l);
        }
        int found = 0;
        foreach (var l in fresh.AllLines())
        {
            string k = KeyOf(l.Cells.FirstOrDefault()?.OriginalText);
            if (k.Length == 0 || !pool.TryGetValue(k, out var q) || q.Count == 0) continue;
            var o = q.Dequeue();
            found++;
            l.Lot = o.Lot; l.LotManual = o.LotManual; l.LotSlot = o.LotSlot; l.NoLot = o.NoLot; l.ForceLot = o.ForceLot;
            if (o.KindChosenByUser) { l.Kind = o.Kind; l.KindChosenByUser = true; }
            foreach (var c in l.Cells)
            {
                var oc = o.Cells.FirstOrDefault(x => x.Column == c.Column);
                if (oc is null || KeyOf(oc.OriginalText) != KeyOf(c.OriginalText) || c.Locked) continue;
                if (oc.IsModified) c.Text = oc.Text;
                c.Strikes = oc.Strikes.Select(x => new Strike { Start = x.Start, Length = x.Length, Color = x.Color }).ToList();
                if (oc.Colors.Count > 0) c.Colors = oc.Colors.Select(x => new Strike { Start = x.Start, Length = x.Length, Color = x.Color }).ToList();
                if (oc.Manual) c.Manual = true;
            }
        }
        for (int i = 0; i < old.Tables.Count && i < fresh.Tables.Count; i++) if (old.Tables[i].LotsOff) fresh.Tables[i].LotsOff = true;
        return found;
    }
}
