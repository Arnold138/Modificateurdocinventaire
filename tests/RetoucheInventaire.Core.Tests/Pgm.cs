using System.Text;
using RetoucheInventaire.Core.Imaging;

namespace RetoucheInventaire.Core.Tests;

/// <summary>Lecture/écriture PGM (format gris le plus simple) pour les tests hors Windows.</summary>
public static class Pgm
{
    public static Raster Read(string path)
    {
        byte[] bytes;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            bytes = ms.ToArray();
        }
        else bytes = File.ReadAllBytes(path);
        int pos = 0;
        string Token()
        {
            while (pos < bytes.Length && (char.IsWhiteSpace((char)bytes[pos]) || bytes[pos] == '#'))
            {
                if (bytes[pos] == '#') while (pos < bytes.Length && bytes[pos] != '\n') pos++;
                else pos++;
            }
            int start = pos;
            while (pos < bytes.Length && !char.IsWhiteSpace((char)bytes[pos])) pos++;
            return Encoding.ASCII.GetString(bytes, start, pos - start);
        }
        if (Token() != "P5") throw new InvalidDataException("PGM binaire (P5) attendu");
        int w = int.Parse(Token()), h = int.Parse(Token()); Token();
        pos++;
        var data = new byte[w * h];
        Array.Copy(bytes, pos, data, 0, data.Length);
        return new Raster(w, h, 1, data);
    }

    public static void Write(string path, Raster r)
    {
        var g = r.ToGray();
        using var fs = File.Create(path);
        var header = Encoding.ASCII.GetBytes($"P5\n{g.Width} {g.Height}\n255\n");
        fs.Write(header);
        fs.Write(g.Data);
    }
}
