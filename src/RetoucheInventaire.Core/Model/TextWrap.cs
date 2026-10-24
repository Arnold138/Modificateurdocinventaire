namespace RetoucheInventaire.Core.Model;

/// <summary>Texte d'une case sur plusieurs lignes, à taille d'écriture constante (même règle que la version iPhone/iPad).</summary>
public static class TextWrap
{
    /// <summary>Désignation (1re colonne) : passe à la ligne ; les valeurs restent sur une ligne.</summary>
    public static bool IsMultiline(Field f) => f.Column == 0;

    /// <summary>
    /// Coupe le texte en lignes qui tiennent dans <paramref name="width"/> (mesure fournie par l'application),
    /// en respectant les retours à la ligne tapés ; un mot trop long est coupé.
    /// </summary>
    public static List<string> Wrap(string text, double width, Func<string, double> measure)
    {
        var output = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string cur = "";
            foreach (var word in para.Split(' '))
            {
                string next = cur.Length == 0 ? word : cur + " " + word;
                if (measure(next) <= width || (cur.Length == 0 && measure(word) <= width)) { cur = next; continue; }
                if (cur.Length > 0) output.Add(cur);
                string rest = word;
                while (measure(rest) > width && rest.Length > 1)
                {
                    int k = rest.Length - 1;
                    while (k > 1 && measure(rest[..k]) > width) k--;
                    output.Add(rest[..k]);
                    rest = rest[k..];
                }
                cur = rest;
            }
            output.Add(cur);
        }
        return output;
    }
}
