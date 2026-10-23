using System.Text;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Export;

public sealed record TxtExportResult(string Text, int LineCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Export texte, livrable principal :
/// une ligne par ligne imprimée, colonnes séparées par des tabulations, titres seuls,
/// UTF-8 avec BOM (pour qu'Excel lise les accents), fins de ligne Windows, nombres sans espace de milliers.
/// </summary>
public static partial class TxtExporter
{
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    [GeneratedRegex(@"^[+-]?\d{1,3}(?:[   ]\d{3})+(?:,\d+)?$")]
    private static partial Regex SpacedNumber();

    [GeneratedRegex(@"^[+-]?\d+(?:[.,]\d+)?$")]
    private static partial Regex PlainNumber();

    public static TxtExportResult Write(InventoryProject project, string path)
    {
        var result = Build(project);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, result.Text, Encoding);
        File.Move(tmp, path, overwrite: true);
        return result;
    }

    public static TxtExportResult Build(InventoryProject project)
    {
        var output = new List<string>();
        var warnings = new List<string>();

        bool lots = Lots.IsActive(project);
        foreach (var table in project.Pages.SelectMany(p => p.Tables))
        {
            var tableLines = new List<string>();
            foreach (var row in table.Rows)
                tableLines.AddRange(ExportRow(row, lots));

            bool startsWithHeader = table.Rows.SelectMany(r => r.Lines).FirstOrDefault(l => l.Kind != LineKind.Ignoree)?.Kind == LineKind.EnTete;
            if (tableLines.Count > 0 && startsWithHeader && output.Count > 0 && output[^1].Length > 0)
                output.Add(""); // une ligne vide sépare deux tableaux
            output.AddRange(tableLines);
        }

        for (int i = 0; i < output.Count; i++)
            foreach (var cell in output[i].Split('\t'))
                if (LooksLikeFormula(cell))
                    warnings.Add($"Ligne {i + 1} : « {Shorten(cell)} » commence par « {cell[0]} » — Excel pourrait le lire comme une formule.");

        var text = output.Count == 0 ? "" : string.Join("\r\n", output) + "\r\n";
        return new TxtExportResult(text, output.Count, warnings);
    }

    private static IEnumerable<string> ExportRow(Row row, bool lots)
    {
        // Avec les lots : une 1re colonne « Lot » sur toutes les lignes, pour garder les colonnes alignées.
        string Prefix(PrintedLine l) => !lots ? "" : (l.Kind == LineKind.EnTete ? "Lot" : l.Lot) + "\t";
        var lines = row.Lines.Where(l => l.Kind != LineKind.Ignoree).ToList();

        // Les valeurs imprimées en face d'un titre descendent sur la première ligne d'article sans valeurs.
        var movedValues = new Dictionary<PrintedLine, string[]>();
        var titlesKeepingValues = new HashSet<PrintedLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != LineKind.Titre || !HasValues(lines[i])) continue;
            var target = lines.Skip(i + 1).FirstOrDefault(l => l.Kind != LineKind.Titre && !HasValues(l) && !movedValues.ContainsKey(l));
            if (target is null) titlesKeepingValues.Add(lines[i]);
            else movedValues[target] = lines[i].Cells.Skip(1).Select(c => c.Text).ToArray();
        }

        foreach (var line in lines)
        {
            if (line.Kind == LineKind.Titre && !titlesKeepingValues.Contains(line))
            {
                var title = CleanText(line.FirstText);
                if (title.Length > 0) yield return Prefix(line) + title;
                continue;
            }

            var cells = line.Cells.Select(c => c.Text).ToArray();
            if (movedValues.TryGetValue(line, out var values))
                for (int c = 1; c < cells.Length && c - 1 < values.Length; c++) cells[c] = values[c - 1];

            var parts = cells.Select((t, c) => c == 0 ? CleanText(t) : CleanValue(t)).ToList();
            while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
            if (parts.Count > 0) yield return Prefix(line) + string.Join("\t", parts);
        }
    }

    private static bool HasValues(PrintedLine l) => l.Cells.Skip(1).Any(c => !string.IsNullOrWhiteSpace(c.Text));

    /// <summary>Texte sur une seule ligne, sans tabulation parasite.</summary>
    public static string CleanText(string s) =>
        Regex.Replace(s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '), " {2,}", " ").Trim();

    /// <summary>Valeur : « 15 000 » devient « 15000 ».</summary>
    public static string CleanValue(string s)
    {
        var t = CleanText(s);
        return SpacedNumber().IsMatch(t) ? Regex.Replace(t, @"[   ]", "") : t;
    }

    private static bool LooksLikeFormula(string cell)
    {
        if (cell.Length < 2) return false;
        char c = cell[0];
        if (c is '=' or '@') return true;
        if (c is '-' or '+') return !cell.StartsWith("+/-") && !PlainNumber().IsMatch(cell);
        return false;
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
