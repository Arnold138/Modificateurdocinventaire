using System.Globalization;
using System.Text;

namespace RetoucheInventaire.Core.Export;

/// <summary>
/// Export PDF : une page par image JPEG (la page telle qu'exportée en JPEG : corrections, lots, totaux).
/// Pas de bibliothèque : le JPEG est mis tel quel dans le PDF (DCTDecode), pleine page, largeur A4 (595 pt),
/// hauteur au prorata. Portage identique dans web/core/pdf.js.
/// </summary>
public static class PdfWriter
{
    private const double A4Width = 595;

    /// <summary>Largeur, hauteur et nombre de composantes (1 gris, 3 couleur, 4 CMJN) lus dans l'en-tête du JPEG.</summary>
    public static (int W, int H, int Comps) JpegInfo(byte[] b)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xff) { i++; continue; }
            int m = b[i + 1];
            if (m == 0xd8 || m == 0x01 || (m >= 0xd0 && m <= 0xd7)) { i += 2; continue; }
            int len = (b[i + 2] << 8) | b[i + 3];
            if (m >= 0xc0 && m <= 0xcf && m != 0xc4 && m != 0xc8 && m != 0xcc)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6], b[i + 9]);
            i += 2 + len;
        }
        throw new InvalidDataException("JPEG illisible");
    }

    /// <summary>Texte PDF : ASCII entre parenthèses, sinon UTF-16 (hexadécimal).</summary>
    private static string PdfText(string s)
    {
        if (s.All(c => c >= 0x20 && c <= 0x7e))
            return "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var sb = new StringBuilder("<FEFF");
        foreach (char c in s) sb.Append(((int)c).ToString("X4"));
        return sb.Append('>').ToString();
    }

    private static string Fmt(double v) => (Math.Round(v * 100) / 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>PDF d'une page par image JPEG, dans l'ordre.</summary>
    public static byte[] Build(IReadOnlyList<byte[]> jpegs, string title = "")
    {
        var output = new MemoryStream();
        var offsets = new long[4 + 3 * jpegs.Count];
        void Put(string s) { var b = Encoding.ASCII.GetBytes(s); output.Write(b); }
        void Obj(int n, string body, byte[]? stream = null)
        {
            offsets[n] = output.Position;
            Put($"{n} 0 obj\n{body}\n");
            if (stream != null) { Put("stream\n"); output.Write(stream); Put("\nendstream\n"); }
            Put("endobj\n");
        }
        int n = jpegs.Count;
        // 1 catalogue, 2 pages, 3 infos, puis par page : page, image, contenu.
        Put("%PDF-1.4\n");
        output.Write([0x25, 0xe2, 0xe3, 0xcf, 0xd3, 0x0a]); // fichier binaire
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Count {n} /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{4 + 3 * i} 0 R"))}] >>");
        Obj(3, $"<< /Title {PdfText(title)} /Producer (Retouche d'inventaire) >>");
        for (int i = 0; i < n; i++)
        {
            var jpeg = jpegs[i];
            var (w, h, comps) = JpegInfo(jpeg);
            double pw = A4Width, ph = A4Width * h / w;
            int p = 4 + 3 * i;
            var content = Encoding.ASCII.GetBytes($"q {Fmt(pw)} 0 0 {Fmt(ph)} 0 0 cm /Im0 Do Q");
            Obj(p, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(pw)} {Fmt(ph)}] /Resources << /XObject << /Im0 {p + 1} 0 R >> >> /Contents {p + 2} 0 R >>");
            string cs = comps == 1 ? "/DeviceGray" : comps == 4 ? "/DeviceCMYK" : "/DeviceRGB";
            Obj(p + 1, $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {cs} /BitsPerComponent 8 /Filter /DCTDecode{(comps == 4 ? " /Decode [1 0 1 0 1 0 1 0]" : "")} /Length {jpeg.Length} >>", jpeg);
            Obj(p + 2, $"<< /Length {content.Length} >>", content);
        }
        long xref = output.Position;
        int count = 4 + 3 * n;
        var table = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int k = 1; k < count; k++) table.Append($"{offsets[k]:D10} 00000 n \n");
        Put(table.ToString());
        Put($"trailer\n<< /Size {count} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
