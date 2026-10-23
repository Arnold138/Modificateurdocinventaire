using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace RetoucheInventaire.App.Services;

/// <summary>Rendu des pages PDF en images, avec le moteur PDF intégré à Windows (aucune dépendance).</summary>
public static class PdfService
{
    /// <summary>Largeur de rendu : environ 200 ppp pour une page A4, suffisant pour l'OCR.</summary>
    private const uint RenderWidth = 1700;

    /// <summary>Pages rendues une par une (une seule en mémoire à la fois), avec leur numéro et le total.</summary>
    public static async IAsyncEnumerable<(int Index, int Count, byte[] Png)> RenderPagesAsync(StorageFile file)
    {
        PdfDocument doc;
        try
        {
            doc = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x8007052B))
        {
            throw new InvalidOperationException("Ce PDF est protégé par un mot de passe.");
        }

        int count = (int)doc.PageCount;
        for (uint i = 0; i < doc.PageCount; i++)
        {
            byte[] png;
            using (var page = doc.GetPage(i))
            using (var stream = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = RenderWidth });
                stream.Seek(0);
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                png = ms.ToArray();
            }
            yield return ((int)i + 1, count, png);
        }
    }
}
