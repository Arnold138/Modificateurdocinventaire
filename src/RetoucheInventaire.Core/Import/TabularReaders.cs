using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace RetoucheInventaire.Core.Import;

/// <summary>Lecture des fichiers TXT et CSV (séparateur deviné : tabulation, point-virgule ou virgule).</summary>
public static class DelimitedReader
{
    public static List<string[]> Read(string path) => Parse(DecodeText(File.ReadAllBytes(path)), Path.GetExtension(path));

    /// <summary>UTF-8 si possible (avec ou sans BOM), sinon Windows-1252.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static List<string[]> Parse(string text, string extension = ".txt")
    {
        var sample = string.Join("\n", text.Split('\n').Take(20));
        char? sep = sample.Contains('\t') ? '\t'
                  : sample.Contains(';') ? ';'
                  : extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) && sample.Contains(',') ? ','
                  : null;

        var rows = new List<string[]>();
        if (sep is null)
        {
            foreach (var l in text.Replace("\r\n", "\n").Split('\n')) rows.Add([l.TrimEnd('\r')]);
        }
        else
        {
            // Analyse type CSV : guillemets, guillemets doublés, retours à la ligne entre guillemets.
            var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add([.. row]); row.Clear(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add([.. row]); }
        }
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }
}

/// <summary>
/// Lecture minimale d'un classeur XLSX (valeurs uniquement, une liste de lignes par feuille),
/// sans bibliothèque externe : un .xlsx est un zip de fichiers XML.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<(string Sheet, List<string[]> Rows)> Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static List<(string Sheet, List<string[]> Rows)> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst != null)
            foreach (var si in Load(sst).Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));

        var rels = Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!).Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        var result = new List<(string, List<string[]>)>();
        foreach (var sheet in Load(zip.GetEntry("xl/workbook.xml")!).Root!.Descendants(Main + "sheet"))
        {
            var target = rels[(string)sheet.Attribute(Rel + "id")!].TrimStart('/');
            var entry = zip.GetEntry(target.StartsWith("xl/") ? target : "xl/" + target);
            if (entry is null) continue;

            var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
            int autoRow = 0;
            foreach (var r in Load(entry).Root!.Descendants(Main + "row"))
            {
                int rowIndex = int.TryParse((string?)r.Attribute("r"), out var ri) ? ri - 1 : autoRow;
                autoRow = rowIndex + 1;
                var cells = new SortedDictionary<int, string>();
                int autoCol = 0;
                foreach (var c in r.Elements(Main + "c"))
                {
                    int col = (string?)c.Attribute("r") is string reference ? ColumnIndex(reference) : autoCol;
                    autoCol = col + 1;
                    cells[col] = CellValue(c, shared);
                }
                rows[rowIndex] = cells;
            }
            if (rows.Count == 0) continue;

            int width = rows.Values.Where(c => c.Count > 0).Select(c => c.Keys.Max() + 1).DefaultIfEmpty(1).Max();
            var list = new List<string[]>();
            for (int i = 0; i <= rows.Keys.Max(); i++)
            {
                var arr = new string[width];
                for (int j = 0; j < width; j++)
                    arr[j] = rows.TryGetValue(i, out var cs) && cs.TryGetValue(j, out var v) ? v : "";
                list.Add(arr);
            }
            while (list.Count > 0 && list[^1].All(string.IsNullOrWhiteSpace)) list.RemoveAt(list.Count - 1);
            result.Add(((string?)sheet.Attribute("name") ?? "Feuille", list));
        }
        return result;
    }

    private static XDocument Load(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return XDocument.Load(s);
    }

    private static int ColumnIndex(string reference)
    {
        int n = 0;
        foreach (char ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }

    private static string CellValue(XElement c, List<string> shared)
    {
        string type = (string?)c.Attribute("t") ?? "n";
        string v = c.Element(Main + "v")?.Value ?? "";
        switch (type)
        {
            case "s": return int.TryParse(v, out var i) && i < shared.Count ? shared[i] : "";
            case "inlineStr": return string.Concat(c.Descendants(Main + "t").Select(t => t.Value));
            case "b": return v == "1" ? "VRAI" : "FAUX";
            case "str":
            case "e": return v;
            default:
                // Nombre : entier tel quel, décimal avec une virgule (usage français).
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d == Math.Floor(d) && Math.Abs(d) < 1e15
                        ? ((long)d).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("0.##########", CultureInfo.InvariantCulture).Replace('.', ',');
                return v;
        }
    }
}
