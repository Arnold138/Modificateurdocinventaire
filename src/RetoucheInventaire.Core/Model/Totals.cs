using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Export;

namespace RetoucheInventaire.Core.Model;

/// <summary>
/// Totaux recalculés : une case de total suit les changements de sa rubrique (valeurs modifiées, lots 0 retirés),
/// tant qu'on ne l'a pas tapée à la main (<see cref="Field.Manual"/>). « Recalculer » la remet en automatique.
/// Total = total imprimé + écarts : une erreur de lecture sur une ligne qu'on ne touche pas ne fausse rien.
/// Récapitulatif (« II - STOCK  44 550  9 000 ») : suit le total de sa rubrique ; TOTAL GENERAL suit le récapitulatif.
/// Même calcul que totals.js (version iPhone/iPad).
/// </summary>
public static partial class Totals
{
    [GeneratedRegex(@"^[+-]?[0-9SOolI]+(?:[.,][0-9]{1,2})?$")]
    private static partial Regex OcrNumber();

    [GeneratedRegex(@"^[+-]?[0-9]+(?:[.,][0-9]{1,2})?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\s*[IVX]{1,5}\s*[-–.]\s*")]
    private static partial Regex RomanPrefix();

    /// <summary>« 15 000 » → 1500000 (centimes) ; « mémoire » → 0 ; « S 500 » (lecture) → 550000 ; null si ce n'est pas un montant.</summary>
    public static long? ParseAmount(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Equals("mémoire", StringComparison.OrdinalIgnoreCase) || t.Equals("memoire", StringComparison.OrdinalIgnoreCase)) return 0;
        t = string.Concat(t.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != ' '));
        if (t.EndsWith('€')) t = t[..^1];
        if (!t.Any(c => c is >= '0' and <= '9')) return null;
        if (OcrNumber().IsMatch(t)) t = t.Replace('S', '5').Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1');
        if (!Number().IsMatch(t)) return null;
        var v = decimal.Parse(t.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return (long)decimal.Round(v * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>19831000 (centimes) → « 198 310 » ; décimales à la virgule si besoin.</summary>
    public static string FormatAmount(long cents)
    {
        bool neg = cents < 0;
        long v = Math.Abs(cents);
        long units = v / 100, dec = v % 100;
        var digits = units.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(' ');
            sb.Append(digits[i]);
        }
        return (neg ? "-" : "") + sb + (dec != 0 ? "," + dec.ToString("00", CultureInfo.InvariantCulture) : "");
    }

    /// <summary>Nom d'une rubrique, sans son numéro romain ni accents : « II - STOCK » → « STOCK ».</summary>
    public static string SectionName(string? s)
    {
        var d = RomanPrefix().Replace(s ?? "", "", 1).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var u = char.ToUpperInvariant(c);
            if (u is >= 'A' and <= 'Z') sb.Append(u);
        }
        return sb.ToString();
    }

    /// <summary>Ligne de récapitulatif : rubrique numérotée (« II - STOCK ») avec des montants, hors en-tête.</summary>
    public static bool IsRecapLine(PrintedLine l) =>
        l.Kind is not (LineKind.EnTete or LineKind.Total) && LineClassifier.IsRomanHeading(l.FirstText) && l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text));

    /// <summary>Case calculée : montant d'une ligne de total ou de récapitulatif.</summary>
    public static bool IsComputedCell(PrintedLine line, Field f) => f.Column > 0 && (line.Kind == LineKind.Total || IsRecapLine(line));

    private static List<PrintedLine> DocumentLines(InventoryProject project) =>
        project.Pages.SelectMany(p => p.Tables).SelectMany(t => Lots.RowOrder(t).Select(r => t.Rows[r]))
            .SelectMany(r => r.Lines).Where(l => !l.OutsideTable && l.Kind != LineKind.Ignoree).ToList();

    /// <summary>Cases retirées des totaux : objets au lot 0, leurs lignes de suite et les valeurs de titre qu'ils reprennent.</summary>
    private static HashSet<Field> RemovedCells(List<PrintedLine> lines, Arrangement.Plan? plan, HashSet<PrintedLine> deleted)
    {
        var removed = new HashSet<Field>();
        foreach (var u in plan?.Units ?? [])
        {
            if (u.Kind == "sort") continue; // trié par numéro : déplacé, toujours dans la vente
            foreach (var c in u.Lines.SelectMany(l => l.Cells)) removed.Add(c);
            if (u.Title != null) foreach (var c in u.Title.Cells.Where(c => c.Column > 0)) removed.Add(c);
        }
        foreach (var l in lines.Where(l => Lots.HasCell(l) && Lots.IsZero(l))) foreach (var c in l.Cells) removed.Add(c);
        foreach (var l in deleted) foreach (var c in l.Cells) removed.Add(c);
        return removed;
    }

    private sealed class Section
    {
        public List<PrintedLine> Items { get; } = [];
        public PrintedLine? Total { get; set; }
    }

    /// <summary>
    /// Recalcule les cases de total et de récapitulatif qui ne sont pas tapées à la main.
    /// <paramref name="plan"/> (facultatif) : la mise en page des lots 0 (pour retirer aussi les valeurs de titre qu'ils emportent).
    /// Renvoie les cases dont le texte a changé.
    /// </summary>
    public static List<Field> Recompute(InventoryProject project, Arrangement.Plan? plan = null)
    {
        var lines = DocumentLines(project);
        var removed = RemovedCells(lines, plan, Arrangement.DeletedLines(project));
        var changed = new List<Field>();
        static IEnumerable<Field> Cols(PrintedLine l) => l.Cells.Where(c => c.Column > 0);
        static Field? CellOf(PrintedLine l, int c) => l.Cells.FirstOrDefault(x => x.Column == c);
        // Ligne dupliquée (rangée ajoutée) : ses montants ne sont pas dans le total imprimé, ils comptent en entier.
        var copied = lines.Where(l => l.Copy).SelectMany(l => l.Cells).ToHashSet();
        long Delta(Field f)
        {
            long o = copied.Contains(f) ? 0 : ParseAmount(f.OriginalText) ?? 0;
            if (removed.Contains(f)) return -o;
            return (ParseAmount(f.Text) ?? 0) - o;
        }
        long Current(Field f) => removed.Contains(f) ? 0 : ParseAmount(f.Text) ?? 0;
        long SumDelta(IEnumerable<PrintedLine> items, int c) => items.Sum(l => CellOf(l, c) is Field x ? Delta(x) : 0);
        long SumCurrent(IEnumerable<PrintedLine> items, int c) => items.Sum(l => CellOf(l, c) is Field x ? Current(x) : 0);
        void SetAuto(Field f, long d, Func<long> fallback)
        {
            if (f.Manual) return;
            var text = f.OriginalText;
            if (d != 0)
            {
                var o = ParseAmount(f.OriginalText);
                text = o is long ov ? FormatAmount(ov + d) : FormatAmount(fallback());
            }
            if (f.Text != text) { f.Text = text; changed.Add(f); }
        }

        // Rangée de chaque total : de l'en-tête ou du total précédent jusqu'à lui.
        var totals = new List<(PrintedLine Line, List<PrintedLine> Items)>();
        int start = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TotalScope != null) continue; // ligne TOTAL ajoutée : calculée à part, ne coupe pas les rubriques imprimées
            if (lines[i].Kind == LineKind.EnTete) start = i + 1;
            else if (lines[i].Kind == LineKind.Total)
            {
                totals.Add((lines[i], lines.GetRange(start, i - start).Where(x => x.Kind != LineKind.EnTete && x.TotalScope is null).ToList()));
                start = i + 1;
            }
        }
        void ComputeTotals()
        {
            foreach (var (line, items) in totals)
                foreach (var f in Cols(line))
                    SetAuto(f, SumDelta(items, f.Column), () => SumCurrent(items, f.Column));
        }

        // Rubriques : de leur en-tête (« II - STOCK ») au suivant (un en-tête répété continue la même rubrique).
        var sections = new Dictionary<string, Section>();
        Section? cur = null;
        foreach (var l in lines)
        {
            if (l.TotalScope != null) continue;
            if (l.Kind == LineKind.EnTete)
            {
                var name = SectionName(l.FirstText);
                cur = name.Length == 0 ? null : sections.TryGetValue(name, out var s) ? s : new Section();
                if (cur != null) sections[name] = cur;
                continue;
            }
            if (cur is null) continue;
            if (l.Kind == LineKind.Total) { cur.Total ??= l; continue; }
            if (!IsRecapLine(l)) cur.Items.Add(l);
        }

        ComputeTotals();
        foreach (var l in lines.Where(IsRecapLine))
        {
            if (!sections.TryGetValue(SectionName(l.FirstText), out var sec)) continue;
            foreach (var f in Cols(l))
            {
                var t = sec.Total is null ? null : CellOf(sec.Total, f.Column);
                long d = t != null ? (ParseAmount(t.Text) ?? 0) - (ParseAmount(t.OriginalText) ?? 0) : SumDelta(sec.Items, f.Column);
                SetAuto(f, d, () => t != null ? ParseAmount(t.Text) ?? 0 : SumCurrent(sec.Items, f.Column));
            }
        }
        ComputeTotals();
        AddedTotals(project, Current, changed);
        return changed;
    }

    /// <summary>
    /// Lignes TOTAL ajoutées : somme des montants actuels, colonne par colonne, selon leur choix (<see cref="TotalScope"/>).
    /// Lots 0 et lignes supprimées : comptés 0. Auto = false : à taper.
    /// </summary>
    private static void AddedTotals(InventoryProject project, Func<Field, long> current, List<Field> changed)
    {
        static bool Countable(PrintedLine x) => !x.OutsideTable && x.Kind is LineKind.Article or LineKind.Titre && x.TotalScope is null && !IsRecapLine(x);
        static IEnumerable<PrintedLine> LinesOf(Page pg) => pg.Tables.SelectMany(t => Lots.RowOrder(t).SelectMany(r => t.Rows[r].Lines));
        foreach (var page in project.Pages)
            foreach (var t in page.Tables)
            {
                var ordered = Lots.RowOrder(t).SelectMany(r => t.Rows[r].Lines).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var l = ordered[i];
                    var sc = l.TotalScope;
                    if (sc is null || !sc.Auto) continue;
                    IEnumerable<PrintedLine> items;
                    if (sc.Mode == "page") items = LinesOf(page);
                    else if (sc.Mode == "all") items = project.Pages.SelectMany(LinesOf);
                    else if (sc.Mode == "pages") items = project.Pages.Where(pg => sc.Pages.Contains(pg.Id)).SelectMany(LinesOf);
                    else
                    {
                        int from = 0;
                        for (int j = i - 1; j >= 0; j--) if (ordered[j].Kind is LineKind.EnTete or LineKind.Total) { from = j + 1; break; }
                        items = ordered.GetRange(from, i - from);
                    }
                    var list = items.Where(Countable).ToList();
                    foreach (var f in l.Cells.Where(c => c.Column > 0))
                    {
                        if (f.Manual) continue;
                        string text = FormatAmount(list.Sum(x => x.Cells.FirstOrDefault(c => c.Column == f.Column) is Field c ? current(c) : 0));
                        if (f.Text != text) { f.Text = text; changed.Add(f); }
                    }
                }
            }
    }

    /// <summary>Montant calculé d'une case (pour afficher l'écart avec un total tapé à la main).</summary>
    public static string ComputedValue(InventoryProject project, Arrangement.Plan? plan, Field f)
    {
        bool was = f.Manual;
        var text = f.Text;
        f.Manual = false;
        Recompute(project, plan);
        var value = f.Text;
        f.Manual = was;
        f.Text = text;
        Recompute(project, plan);
        return value;
    }
}
