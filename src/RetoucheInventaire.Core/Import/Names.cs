using System.Text;

namespace RetoucheInventaire.Core.Import;

public static class Names
{
    /// <summary>
    /// Répare un nom de fichier dont les accents ont été abîmés en route (« prisÃ© » → « prisé ») :
    /// des octets UTF-8 relus comme du Latin-1.
    /// </summary>
    /// <summary>Tri « naturel » des noms : « page 2 » avant « page 10 », sans tenir compte des majuscules.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            int d = string.Compare(a[i].ToString(), b[j].ToString(), System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
            if (d != 0) return d;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static string Repair(string name)
    {
        // iPhone/Mac : noms décomposés (« A » + tilde) ; on les recompose d'abord.
        name = name.Normalize(NormalizationForm.FormC);
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
