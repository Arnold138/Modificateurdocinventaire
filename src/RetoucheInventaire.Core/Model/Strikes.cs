namespace RetoucheInventaire.Core.Model;

/// <summary>Un morceau de texte barré dans une case : caractères [Start, Start + Length[ du texte, trait rouge ou noir.</summary>
public sealed class Strike
{
    public int Start { get; set; }
    public int Length { get; set; }
    /// <summary>« red » ou « black » (couleur du trait seulement ; le texte garde sa couleur).</summary>
    public string Color { get; set; } = Strikes.Red;
}

/// <summary>
/// Texte barré, comme dans Word : un trait fin au milieu des lettres sur le morceau choisi.
/// Le .txt n'est pas touché (le texte barré y reste tel quel) ; seul l'affichage et le JPEG le montrent.
/// </summary>
public static class Strikes
{
    public const string Red = "red";
    public const string Black = "black";

    /// <summary>Tout le morceau [start, end[ est-il déjà barré ?</summary>
    public static bool IsStruck(Field f, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (!f.Strikes.Any(s => i >= s.Start && i < s.Start + s.Length)) return false;
        return end > start;
    }

    /// <summary>
    /// Bouton « Barrer » : barre le morceau sélectionné (tout le texte s'il n'y a pas de sélection),
    /// ou enlève le trait s'il était déjà entièrement barré.
    /// </summary>
    public static void Toggle(Field f, int start, int end, string color)
    {
        if (end <= start) { start = 0; end = f.Text.Length; }
        start = Math.Clamp(start, 0, f.Text.Length);
        end = Math.Clamp(end, start, f.Text.Length);
        if (end == start) return;
        bool struck = IsStruck(f, start, end);
        Cut(f, start, end);
        if (!struck) f.Strikes.Add(new Strike { Start = start, Length = end - start, Color = color == Black ? Black : Red });
        Normalize(f);
    }

    /// <summary>Retire le trait sur [start, end[ (les morceaux de part et d'autre restent barrés).</summary>
    private static void Cut(Field f, int start, int end)
    {
        var kept = new List<Strike>();
        foreach (var s in f.Strikes)
        {
            int a = s.Start, b = s.Start + s.Length;
            if (b <= start || a >= end) { kept.Add(s); continue; }
            if (a < start) kept.Add(new Strike { Start = a, Length = start - a, Color = s.Color });
            if (b > end) kept.Add(new Strike { Start = end, Length = b - end, Color = s.Color });
        }
        f.Strikes = kept;
    }

    /// <summary>
    /// Le texte a été modifié : les traits suivent les caractères (texte tapé au milieu d'un morceau barré : barré aussi ;
    /// morceau effacé : trait retiré).
    /// </summary>
    public static void Adjust(Field f, string oldText, string newText)
    {
        if (f.Strikes.Count == 0 || oldText == newText) return;
        int p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        int sfx = 0;
        while (sfx < oldText.Length - p && sfx < newText.Length - p && oldText[^(sfx + 1)] == newText[^(sfx + 1)]) sfx++;
        int oldEnd = oldText.Length - sfx, ins = newText.Length - sfx - p, delta = newText.Length - oldText.Length;
        int MapStart(int x) => x <= p ? x : x >= oldEnd ? x + delta : p + ins;
        int MapEnd(int x) => x <= p ? x : x >= oldEnd ? x + delta : p;
        foreach (var s in f.Strikes)
        {
            int a = MapStart(s.Start), b = MapEnd(s.Start + s.Length);
            s.Start = a;
            s.Length = b - a;
        }
        Normalize(f, newText.Length);
    }

    private static void Normalize(Field f, int? length = null)
    {
        int n = length ?? f.Text.Length;
        var list = f.Strikes
            .Select(s => new Strike { Start = Math.Clamp(s.Start, 0, n), Length = Math.Clamp(s.Start + s.Length, 0, n) - Math.Clamp(s.Start, 0, n), Color = s.Color })
            .Where(s => s.Length > 0).OrderBy(s => s.Start).ToList();
        // Morceaux qui se touchent, même couleur : un seul trait.
        var merged = new List<Strike>();
        foreach (var s in list)
        {
            var last = merged.LastOrDefault();
            if (last != null && last.Color == s.Color && s.Start <= last.Start + last.Length)
                last.Length = Math.Max(last.Length, s.Start + s.Length - last.Start);
            else merged.Add(s);
        }
        f.Strikes = merged;
    }
}
