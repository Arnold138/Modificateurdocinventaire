using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Analysis;

/// <summary>
/// Devine le rôle de chaque ligne (titre, total, en-tête, article).
/// L'utilisateur peut toujours corriger d'un toucher ; ses choix ne sont jamais écrasés.
/// </summary>
public static partial class LineClassifier
{
    [GeneratedRegex(@"^\s*(\+/-\s*)?\d{1,3}\s+\p{L}")]
    private static partial Regex ItemStart();

    [GeneratedRegex(@"^\s*[IVX]{1,5}\s*[-–.]\s*\S")]
    private static partial Regex RomanHeadingRegex();

    /// <summary>« II - STOCK », « IV - MATERIEL … » : rubrique numérotée en chiffres romains (en-tête ou ligne de récapitulatif).</summary>
    public static bool IsRomanHeading(string? s) => RomanHeadingRegex().IsMatch(s ?? "");

    /// <summary>Ressemble à un objet : « 1 gerbeur… », « +/- 20 palettes… ».</summary>
    public static bool LooksLikeItem(string s) => ItemStart().IsMatch(s);

    public static void Classify(Table table)
    {
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var lines = table.Rows[r].Lines;
            // En-tête : 1re rangée, ou rangée de libellés (« exploitation », « réalisation ») sans désignation (récapitulatif).
            bool header = IsHeaderRow(lines) && (r == 0 || lines.All(l => string.IsNullOrWhiteSpace(l.Cells.FirstOrDefault()?.Text) || IsRomanHeading(l.Cells[0].Text)));
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.KindChosenByUser || line.OutsideTable) continue;
                if (header) { line.Kind = LineKind.EnTete; continue; }

                string first = line.Cells.Count > 0 ? line.Cells[0].Text.Trim() : "";
                bool valuesEmpty = line.Cells.Skip(1).All(c => string.IsNullOrWhiteSpace(c.Text));
                bool singleSegment = line.Cells.Count(c => !string.IsNullOrWhiteSpace(c.Text)) == 1;

                if (StartsWithTotal(first)) line.Kind = LineKind.Total;
                else if (IsUpperTitle(first) && (
                             (i + 1 < lines.Count && (ItemStart().IsMatch(lines[i + 1].Cells[0].Text) || first.EndsWith(':')))
                             || (lines.Count == 1 && valuesEmpty && (table.HasGrid || singleSegment))))
                    line.Kind = LineKind.Titre;
                else line.Kind = LineKind.Article;
            }
        }
    }

    /// <summary>Première rangée dont les colonnes de valeurs contiennent des libellés (« exploitation »).</summary>
    private static bool IsHeaderRow(List<PrintedLine> lines)
    {
        var values = lines.SelectMany(l => l.Cells.Skip(1)).Select(c => c.Text.Trim()).Where(t => t.Length > 0).ToList();
        return values.Count > 0
               && values.All(v => !v.Any(char.IsDigit))
               && values.Any(v => !v.Equals("mémoire", StringComparison.OrdinalIgnoreCase));
    }

    public static bool StartsWithTotal(string s) =>
        RemoveAccents(s).TrimStart().StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase);

    public static bool IsUpperTitle(string s)
    {
        var letters = s.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(char.IsUpper) >= 0.85 * letters.Count;
    }

    private static string RemoveAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
