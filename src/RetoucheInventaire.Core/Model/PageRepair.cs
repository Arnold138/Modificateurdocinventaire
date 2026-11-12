using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Imaging;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Réparation, à l'ouverture, des pages lues par une version précédente dont le tableau avait perdu une colonne (bord gauche
/// effacé par endroits sur le scan : pages 8, 10, 12 et 17 du client, colonne des désignations absente, impossible d'y écrire),
/// et des pages ajoutées vierges qui avaient perdu cette colonne de leur modèle (grand rectangle blanc inutilisable).
/// Seules les pages jamais retouchées (aucun texte changé, aucune ligne ajoutée, déplacée ou agrandie) sont relues : rien
/// de ce que l'utilisateur a fait n'est perdu. Chaque page n'est vérifiée qu'une fois (<see cref="Page.GridRev"/>).
/// </summary>
public static class PageRepair
{
    /// <summary>Version de la lecture des filets (<see cref="Page.GridRev"/>) : 1 = bord gauche coupé et filets penchés repris.</summary>
    public const int CurrentGrid = 1;

    /// <summary>Page importée pas encore vérifiée avec la lecture actuelle des filets.</summary>
    public static bool Pending(Page page) =>
        page.GridRev < CurrentGrid && page.Source == PageSource.Image && page.Added == null && !page.EndLots;

    /// <summary>Page importée telle que lue : rien de tapé, barré, coloré, ajouté, déplacé, agrandi ou verrouillé à la main.</summary>
    public static bool Untouched(Page page) =>
        page.Inserts.Count == 0 && page.LockZones.Count == 0
        && page.Tables.All(t => t.RowOrder == null && t.Rows.All(r => r.Inserted == null))
        && page.AllLines().All(l => l.Extra == 0 && l.Tall == 0 && !l.Copy && !l.Deleted && !l.KindChosenByUser
            && l.MoveTo == null && l.TotalScope == null && l.EndAt == null && !l.NoLot && !l.ForceLot && !l.LotHere && !l.LotManual
            && l.Lot.Length == 0)
        && page.AllFields().All(f => !f.IsModified && f.Strikes.Count == 0 && f.Colors.Count == 0 && !f.Manual);

    /// <summary>
    /// Relit la page sur son image (<paramref name="image"/> : la page redressée, comme à l'import) avec le texte déjà lu.
    /// La nouvelle lecture n'est gardée que si chaque tableau garde ses colonnes et ses rangées et qu'au moins un tableau
    /// retrouve une colonne. Renvoie vrai si la page a changé. La page est notée vérifiée dans tous les cas.
    /// </summary>
    public static bool Regrid(Page page, Raster image)
    {
        if (!Pending(page)) return false;
        page.GridRev = CurrentGrid;
        if (!Untouched(page) || image.Width != page.Width || image.Height != page.Height) return false;
        var words = page.AllFields().Where(f => !string.IsNullOrWhiteSpace(f.OriginalText))
            .Select(f => new OcrWord(f.OriginalText, f.Ink.IsEmpty ? f.Box : f.Ink)).ToList();
        var fresh = new Page { Id = page.Id, Source = page.Source };
        PageAnalyzer.Analyze(fresh, image, words);
        if (!Gains(page, fresh)) return false;
        // Choix des tableaux à lots : celui du tableau d'origine au même endroit.
        foreach (var t in fresh.Tables)
        {
            var old = page.Tables.FirstOrDefault(o => Overlap(o, t));
            if (old != null) t.LotsOff = old.LotsOff;
        }
        page.Tables = fresh.Tables;
        page.LooseLines = fresh.LooseLines;
        page.Rev = fresh.Rev;
        page.GridRev = CurrentGrid;
        return true;
    }

    /// <summary>Chaque tableau à grille de l'ancienne lecture se retrouve (mêmes filets, au moins autant de colonnes), et un en a plus.</summary>
    internal static bool Gains(Page old, Page fresh)
    {
        static bool Grid(Table t) => t.HasGrid && t.ColumnXs.Count >= 2 && t.RowYs.Count >= 2;
        bool gained = false;
        var olds = old.Tables.Where(Grid).ToList();
        var news = fresh.Tables.Where(Grid).ToList();
        if (olds.Count == 0 || news.Count < olds.Count) return false;
        foreach (var o in olds)
        {
            var n = news.FirstOrDefault(n => o.ColumnXs.All(x => n.ColumnXs.Any(y => Math.Abs(x - y) <= 6))
                && Math.Abs(n.RowYs.Count - o.RowYs.Count) <= 1 && Math.Abs(n.RowYs[^1] - o.RowYs[^1]) <= 12);
            if (n is null) return false;
            if (n.ColumnXs.Count > o.ColumnXs.Count) gained = true;
        }
        return gained;
    }

    private static bool Overlap(Table a, Table b) =>
        Math.Min(a.Bounds.Bottom, b.Bounds.Bottom) - Math.Max(a.Bounds.Top, b.Bounds.Top) > 0.5 * Math.Min(a.Bounds.Height, b.Bounds.Height);

    /// <summary>
    /// Page ajoutée encore vide (rien d'écrit, aucun numéro) dont un tableau a moins de colonnes que son modèle : son modèle
    /// (à recréer vierge à sa place), sinon null.
    /// </summary>
    public static Page? BrokenBlank(InventoryProject project, Page page)
    {
        if (page.Added is not { } info || page.EndLots) return null;
        if (page.AllFields().Any(f => !string.IsNullOrWhiteSpace(f.Text)) || page.AllLines().Any(l => l.Lot.Length > 0)) return null;
        bool lost = page.Tables.Where((t, i) => i < info.Tables.Count && info.Tables[i] is { } a && t.ColumnXs.Count < a.Orig.Count).Any();
        if (!lost) return null;
        var model = project.Pages.FirstOrDefault(p => p.Id == info.From && p.Added == null && !p.EndLots);
        return model != null && AddedPage.CanBeModel(model) ? model : null;
    }
}
