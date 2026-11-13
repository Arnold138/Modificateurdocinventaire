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
        // Même coupure que la zone de saisie de Windows (et le navigateur) : les espaces tapés comptent, un mot qui ne tient pas
        // sur la fin de la ligne passe à la ligne suivante (même si la ligne ne contient que des espaces : « ␣aaaa… » laisse
        // une 1re ligne vide), et seul un mot plus long que la case est coupé en morceaux. Sinon la rangée était calculée une
        // ligne trop courte et le texte débordait sur la suivante.
        var output = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var words = para.Split(' ');
            string cur = "";
            bool open = false; // ligne commencée (même par des espaces)
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                string next = open ? cur + " " + word : word;
                if (measure(next) <= width) { cur = next; open = true; continue; }
                if (open && (cur.Length > 0 || i > 0)) { output.Add(cur); cur = ""; open = false; }
                string rest = word;
                while (measure(rest) > width && rest.Length > 1)
                {
                    int k = rest.Length - 1;
                    while (k > 1 && measure(rest[..k]) > width) k--;
                    output.Add(rest[..k]);
                    rest = rest[k..];
                }
                cur = rest;
                open = true;
            }
            output.Add(cur);
        }
        return output;
    }
}
