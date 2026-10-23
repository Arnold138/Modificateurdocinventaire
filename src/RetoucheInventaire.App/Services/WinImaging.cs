using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using RetoucheInventaire.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RetoucheInventaire.App.Services;

/// <summary>Passerelle entre les bitmaps Windows et le format <see cref="Raster"/> du cœur.</summary>
public static class WinImaging
{
    /// <summary>Taille maximale lue depuis une photo (au-delà, aucun gain pour l'OCR, seulement de la lenteur).</summary>
    private const double MaxSide = 4000;

    /// <summary>Décode une image (JPG, PNG…) en tenant compte de l'orientation EXIF de l'appareil photo.</summary>
    public static async Task<Raster> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        double k = Math.Min(1.0, MaxSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform();
        uint sw = decoder.PixelWidth, sh = decoder.PixelHeight;
        if (k < 1)
        {
            sw = (uint)Math.Round(decoder.PixelWidth * k);
            sh = (uint)Math.Round(decoder.PixelHeight * k);
            transform.ScaledWidth = sw;
            transform.ScaledHeight = sh;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var data = provider.DetachPixelData();

        bool swapped = decoder.OrientedPixelWidth != decoder.PixelWidth;
        int w = (int)(swapped ? sh : sw), h = (int)(swapped ? sw : sh);
        if (data.Length != w * h * 4) (w, h) = (h, w);
        FlattenAlpha(data);
        return new Raster(w, h, 4, data);
    }

    /// <summary>Les PNG transparents sont posés sur du blanc (le papier).</summary>
    private static void FlattenAlpha(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)(bgra[i + c] + (255 - a));
            bgra[i + 3] = 255;
        }
    }

    public static WriteableBitmap ToBitmap(Raster raster)
    {
        var bgra = raster.ToBgra();
        var wb = new WriteableBitmap(bgra.Width, bgra.Height);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(bgra.Data, 0, bgra.Data.Length);
        wb.Invalidate();
        return wb;
    }

    public static SoftwareBitmap ToSoftwareBitmap(Raster raster)
    {
        var bgra = raster.ToBgra();
        return SoftwareBitmap.CreateCopyFromBuffer(bgra.Data.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
    }
}
