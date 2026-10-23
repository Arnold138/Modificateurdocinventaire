namespace RetoucheInventaire.Core.Imaging;

/// <summary>
/// Image brute en mémoire : 1 canal (gris) ou 4 canaux (BGRA, ordre de Windows.Graphics.Imaging).
/// Le cœur ne dépend d'aucune API Windows : l'application convertit ses bitmaps vers ce format.
/// </summary>
public sealed class Raster
{
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public byte[] Data { get; }

    public Raster(int width, int height, int channels, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Taille d'image invalide.");
        if (channels != 1 && channels != 4) throw new ArgumentException("1 ou 4 canaux attendus.");
        if (data.Length != width * height * channels) throw new ArgumentException("Taille des données incohérente.");
        Width = width; Height = height; Channels = channels; Data = data;
    }

    public static Raster CreateWhite(int width, int height, int channels)
    {
        var data = new byte[width * height * channels];
        Array.Fill(data, (byte)255);
        return new Raster(width, height, channels, data);
    }

    public byte this[int x, int y] => Data[y * Width + x]; // gris uniquement

    public Raster ToGray()
    {
        if (Channels == 1) return this;
        var g = new byte[Width * Height];
        for (int i = 0, j = 0; i < g.Length; i++, j += 4)
            g[i] = (byte)((Data[j] * 29 + Data[j + 1] * 150 + Data[j + 2] * 77) >> 8);
        return new Raster(Width, Height, 1, g);
    }

    public Raster ToBgra()
    {
        if (Channels == 4) return this;
        var d = new byte[Width * Height * 4];
        for (int i = 0, j = 0; i < Data.Length; i++, j += 4)
        {
            d[j] = d[j + 1] = d[j + 2] = Data[i];
            d[j + 3] = 255;
        }
        return new Raster(Width, Height, 4, d);
    }
}
