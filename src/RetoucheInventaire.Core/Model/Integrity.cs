using RetoucheInventaire.Core.Export;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Contrôle de la mise en page d'un projet : les mêmes vérifications que le banc d'essai, faites par l'application après
/// chaque action. Si une action casse quelque chose (rangée écrasée, filets dans le désordre, case sortie de sa rangée,
/// morceaux d'image incohérents…), l'application revient d'elle-même à l'état d'avant : un projet cassé n'est jamais gardé.
/// Chaque problème a une clé stable (sans coordonnées) : seuls les problèmes nouveaux comptent, un défaut déjà présent
/// dans un ancien projet ne bloque pas le travail.
/// </summary>
public static class Integrity
{
    /// <summary>Problèmes de mise en page du projet (clé stable → description) ; vide si tout va bien.</summary>
    public static Dictionary<string, string> Check(InventoryProject project)
    {
        var found = new Dictionary<string, string>();
        void Add(string key, string what) => found.TryAdd(key, what);

        var ids = new HashSet<string>();
        for (int k = 0; k < project.Pages.Count; k++)
        {
            var page = project.Pages[k];
            string p = $"page {k + 1}";
            foreach (var f in page.AllFields())
                if (f.Id.Length > 0 && !ids.Add(f.Id)) Add($"double|{f.Id}", $"{p} : case {f.Id} en double");
            if (page.EndLots) continue;
            if (page.Width <= 0 || page.Height <= 0) { Add($"taille|{page.Id}", $"{p} : taille {page.Width} × {page.Height}"); continue; }

            // Image : la suite des morceaux fait exactement la hauteur de la page, sans sortir de l'image d'origine.
            try
            {
                int baseH = PageLayout.BaseHeight(page);
                if (baseH <= 0) Add($"base|{page.Id}", $"{p} : hauteur d'origine {baseH}");
                var seq = PageLayout.Pieces(page);
                int sum = seq.Sum(x => x.N);
                if (sum != page.Height) Add($"morceaux|{page.Id}", $"{p} : image de {sum} px pour une page de {page.Height} px");
                if (seq.Any(x => x.N <= 0 || x.Y0 < 0 || (!x.Extra && !x.Gap && x.Y0 + x.N > baseH)))
                    Add($"morceau|{page.Id}", $"{p} : morceau d'image hors de la page d'origine");
            }
            catch (Exception e) { Add($"image|{page.Id}", $"{p} : image impossible à composer ({e.Message})"); }

            for (int ti = 0; ti < page.Tables.Count; ti++)
            {
                var t = page.Tables[ti];
                if (!t.HasGrid || t.RowYs.Count < 2 || t.ColumnXs.Count < 2) continue;
                string tk = $"{page.Id}|{ti}";
                if (t.Rows.Count + 1 != t.RowYs.Count) Add($"rangees|{tk}", $"{p} : {t.Rows.Count} rangées pour {t.RowYs.Count} filets");
                for (int i = 1; i < t.RowYs.Count; i++)
                    if (!(t.RowYs[i] - t.RowYs[i - 1] >= 2)) { Add($"filets|{tk}", $"{p} : rangée écrasée ou filets dans le désordre"); break; }
                if (t.RowOrder is { } order && (order.Count != t.Rows.Count || order.Distinct().Count() != order.Count || order.Any(r => r < 0 || r >= t.Rows.Count)))
                    Add($"ordre|{tk}", $"{p} : ordre des rangées invalide");
                for (int r = 0; r < t.Rows.Count && r + 1 < t.RowYs.Count; r++)
                    foreach (var l in t.Rows[r].Lines)
                    {
                        foreach (var f in l.Cells)
                        {
                            double cy = f.Box.Y + (f.Box.Height - l.Extra) / 2;
                            if (!(cy > t.RowYs[r] - 1 && cy < t.RowYs[r + 1] + 1))
                                Add($"case|{f.Id}", $"{p} : « {Short(l.FirstText)} » sortie de sa rangée");
                        }
                        if (l.Extra > 0 && t.RowYs[r + 1] < l.ExtraAt + l.Extra - 1)
                            Add($"agrandie|{l.Cells.FirstOrDefault()?.Id}", $"{p} : « {Short(l.FirstText)} » agrandie hors de sa rangée");
                    }
            }
            var bands = new HashSet<string>(page.Inserts.Select(b => b.Id));
            foreach (var row in page.Tables.SelectMany(t => t.Rows))
                if (row.Inserted != null && !bands.Contains(row.Inserted)) Add($"bande|{page.Id}|{row.Inserted}", $"{p} : rangée ajoutée sans sa bande");
        }

        // Écran et export : la mise en page se calcule, chaque page s'affiche, chaque objet a sa case de lot.
        try
        {
            if (Lots.IsActive(project))
                foreach (var o in Lots.ObjectLines(project))
                    if (!Lots.HasCell(o.Line)) Add($"lot|{o.Line.Cells.FirstOrDefault()?.Id}", $"« {Short(o.Line.FirstText)} » : objet sans case de lot");
            var plan = Arrangement.Build(project);
            if (plan.Pages.Count == project.Pages.Count)
                for (int k = 0; k < project.Pages.Count; k++)
                {
                    if (plan.Pages[k].Height <= 0) Add($"plan|{project.Pages[k].Id}", $"page {k + 1} : hauteur {plan.Pages[k].Height}");
                    var v = Arrangement.ViewOf(project, plan, k);
                    if (Lots.IsActive(project))
                        foreach (var e in v.Lines)
                            if (!e.CarriedOnly && Lots.HasCell(e.Line) && !e.Table.LotsOff && e.LotBox is null)
                                Add($"ecran|{e.Line.Cells.FirstOrDefault()?.Id}", $"page {k + 1} : « {Short(e.Line.FirstText)} » sans case de lot à l'écran");
                }
            TxtExporter.Build(project);
        }
        catch (Exception e) { Add("plan", $"mise en page impossible à calculer ({e.GetType().Name} : {e.Message})"); }
        return found;
    }

    /// <summary>Problèmes apparus depuis <paramref name="before"/> (ceux qui existaient déjà ne comptent pas).</summary>
    public static List<string> NewProblems(Dictionary<string, string> before, Dictionary<string, string> after) =>
        after.Where(x => !before.ContainsKey(x.Key)).Select(x => x.Value).ToList();

    private static string Short(string t)
    {
        t = t.Replace('\n', ' ');
        return t.Length > 40 ? t[..40] + "…" : t;
    }
}
