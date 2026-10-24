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
        f.Strikes = AdjustList(f.Strikes, oldText, newText);
        f.Colors = AdjustList(f.Colors, oldText, newText);
    }

    private static List<Strike> AdjustList(List<Strike> list, string oldText, string newText)
    {
        if (list.Count == 0 || oldText == newText) return list;
        int p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        int sfx = 0;
        while (sfx < oldText.Length - p && sfx < newText.Length - p && oldText[^(sfx + 1)] == newText[^(sfx + 1)]) sfx++;
        int oldEnd = oldText.Length - sfx, ins = newText.Length - sfx - p, delta = newText.Length - oldText.Length;
        int MapStart(int x) => x <= p ? x : x >= oldEnd ? x + delta : p + ins;
        int MapEnd(int x) => x <= p ? x : x >= oldEnd ? x + delta : p;
        foreach (var s in list)
        {
            int a = MapStart(s.Start), b = MapEnd(s.Start + s.Length);
            s.Start = a;
            s.Length = b - a;
        }
        return Normalized(list, newText.Length);
    }

    private static void Normalize(Field f, int? length = null) => f.Strikes = Normalized(f.Strikes, length ?? f.Text.Length);

    private static List<Strike> Normalized(List<Strike> source, int n)
    {
        var list = source
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
        return merged;
    }

    // ---------- Couleur du texte (rouge, bleu, vert) : sur la sélection, ou toute la case ----------

    /// <summary>Couleurs proposées (« noir » = écriture normale).</summary>
    public static readonly IReadOnlyDictionary<string, (byte R, byte G, byte B)> TextColors = new Dictionary<string, (byte, byte, byte)>
    {
        ["red"] = (0xd4, 0x00, 0x00),
        ["blue"] = (0x00, 0x50, 0xc8),
        ["green"] = (0x10, 0x7c, 0x10),
    };

    public static (byte R, byte G, byte B) TextRgb(string? color) => color != null && TextColors.TryGetValue(color, out var c) ? c : ((byte)0, (byte)0, (byte)0);

    /// <summary>La sélection est-elle entièrement de cette couleur ?</summary>
    public static bool HasColor(Field f, int start, int end, string color)
    {
        for (int i = start; i < end; i++)
            if (!f.Colors.Any(s => s.Color == color && i >= s.Start && i < s.Start + s.Length)) return false;
        return end > start;
    }

    /// <summary>
    /// Met la sélection (toute la case sans sélection) dans une couleur ; « black » (ou la même couleur une 2e fois)
    /// remet l'écriture normale. Renvoie vrai si la couleur est posée.
    /// </summary>
    public static bool SetColor(Field f, int start, int end, string color)
    {
        if (end <= start) { start = 0; end = f.Text.Length; }
        start = Math.Clamp(start, 0, f.Text.Length);
        end = Math.Clamp(end, start, f.Text.Length);
        if (end == start) return false;
        bool remove = !TextColors.ContainsKey(color) || HasColor(f, start, end, color);
        var kept = new List<Strike>();
        foreach (var s in f.Colors)
        {
            int a = s.Start, b = s.Start + s.Length;
            if (b <= start || a >= end) { kept.Add(s); continue; }
            if (a < start) kept.Add(new Strike { Start = a, Length = start - a, Color = s.Color });
            if (b > end) kept.Add(new Strike { Start = end, Length = b - end, Color = s.Color });
        }
        if (!remove) kept.Add(new Strike { Start = start, Length = end - start, Color = color });
        f.Colors = Normalized(kept, f.Text.Length);
        return !remove;
    }

    /// <summary>Morceaux [Start, End[ du texte avec leur couleur (null = noir), dans l'ordre.</summary>
    public static List<(int Start, int End, string? Color)> ColorRuns(Field f, int from = 0, int to = -1)
    {
        if (to < 0) to = f.Text.Length;
        var cuts = new SortedSet<int> { from, to };
        foreach (var s in f.Colors) { cuts.Add(Math.Clamp(s.Start, from, to)); cuts.Add(Math.Clamp(s.Start + s.Length, from, to)); }
        var xs = cuts.ToList();
        var runs = new List<(int Start, int End, string? Color)>();
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            int a = xs[i], b = xs[i + 1];
            if (b <= a) continue;
            var c = f.Colors.FirstOrDefault(s => a >= s.Start && a < s.Start + s.Length)?.Color;
            if (runs.Count > 0 && runs[^1].Color == c && runs[^1].End == a) runs[^1] = (runs[^1].Start, b, c);
            else runs.Add((a, b, c));
        }
        return runs;
    }
}
