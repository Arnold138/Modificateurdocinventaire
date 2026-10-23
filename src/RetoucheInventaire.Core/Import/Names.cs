using System.Text;

namespace RetoucheInventaire.Core.Import;

public static class Names
{
    /// <summary>
    /// Répare un nom de fichier dont les accents ont été abîmés en route (« prisÃ© » → « prisé ») :
    /// des octets UTF-8 relus comme du Latin-1.
    /// </summary>
    public static string Repair(string name)
    {
        if (name.IndexOfAny(['Ã', 'Â']) < 0) return name;
        try
        {
            var bytes = name.Select(c => c <= 0xFF ? (byte)c : throw new ArgumentException()).ToArray();
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception)
        {
            return name;
        }
    }
}
