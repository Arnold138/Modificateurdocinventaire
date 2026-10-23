using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace RetoucheInventaire.App.Services;

/// <summary>Rendu des pages PDF en images, avec le moteur PDF intégré à Windows (aucune dépendance).</summary>
public static class PdfService
{
    /// <summary>Largeur de rendu : environ 200 ppp pour une page A4, suffisant pour l'OCR.</summary>
    private const uint RenderWidth = 1700;

    public static async Task<List<byte[]>> RenderPagesAsync(StorageFile file)
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

        var pages = new List<byte[]>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = RenderWidth });
            stream.Seek(0);
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            pages.Add(ms.ToArray());
        }
        return pages;
    }
}
