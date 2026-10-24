namespace RetoucheInventaire.Core.Model;

/// <summary>Un trait de texte barré, en pixels de page.</summary>
public readonly record struct StrikeSegment(double X0, double X1, double Y, double Thickness, string Color);

/// <summary>
/// Placement du texte d'une case (même règle à l'écran et à l'export, et sur iPhone/iPad) :
/// désignation à la taille de l'imprimé sur plusieurs lignes, valeurs sur une ligne réduites si besoin,
/// 1re ligne à la hauteur du texte d'origine. La mesure des textes est fournie par l'application.
/// </summary>
public static class TextPlacement
{
    public const double LineHeightRatio = PageLayout.LineHeightRatio;

    /// <summary>Lignes imprimées en gras : totaux et en-têtes (« TOTAL STOCK », « II - STOCK »).</summary>
    public static bool IsBold(PrintedLine? line) => line?.Kind is LineKind.Total or LineKind.EnTete;

    /// <summary>
    /// Morceaux d'écriture d'origine à mettre en couleur (texte non retapé) : rectangles en pixels de page,
    /// position des lettres estimée d'après la largeur de l'encre.
    /// </summary>
    public static List<(int X0, int Y0, int X1, int Y1, string Color)> ColorRects(PrintedLine line, Field f, double baseSize, Func<string, double, double> measure)
    {
        var output = new List<(int, int, int, int, string)>();
        if (f.Colors.Count == 0 || f.Locked || f.IsModified || f.Ink.IsEmpty || f.Text.Contains('\n')) return output;
        double total = measure(f.Text, baseSize);
        double k = total > 0 ? f.Ink.Width / total : 1;
        foreach (var (start, end, color) in Strikes.ColorRuns(f))
        {
            if (color is null) continue;
            int x0 = (int)Math.Floor(f.Ink.X + measure(f.Text[..start], baseSize) * k) - 1;
            int x1 = (int)Math.Ceiling(f.Ink.X + measure(f.Text[..end], baseSize) * k) + 1;
            output.Add((x0, (int)Math.Floor(f.Ink.Top) - 2, x1, (int)Math.Ceiling(f.Ink.Bottom) + 2, color));
        }
        return output;
    }

    public static (double Size, List<string> Lines, double Top) Layout(PrintedLine line, Field f, string text, double baseSize, Func<string, double, double> measure)
    {
        var b = PageLayout.BaseBox(line, f);
        double size = baseSize;
        double avail = Math.Max(4, f.Box.Width - f.Indent - 3);
        List<string> lines;
        if (TextWrap.IsMultiline(f)) lines = TextWrap.Wrap(text, avail, s => measure(s, size));
        else
        {
            string t = text.Length == 0 ? " " : text;
            while (size > 5 && measure(t, size) > avail) size -= 0.5;
            lines = [text];
        }
        double center = f.Ink.IsEmpty ? b.CenterY : f.Ink.CenterY;
        double top = Math.Clamp(center - 0.58 * size, b.Top, Math.Max(b.Top, b.Bottom - LineHeightRatio * size));
        return (size, lines, top);
    }

    /// <summary>Abscisse de départ d'une ligne de texte selon l'alignement de la case.</summary>
    public static double LineX(Field f, double width) => f.Align switch
    {
        TextAlign.Right => f.Box.Right - f.Indent - width,
        TextAlign.Center => f.Box.X + (f.Box.Width - width) / 2,
        _ => f.Box.X + f.Indent,
    };

    /// <summary>
    /// Traits des morceaux barrés. Texte d'origine non retouché : posé sur l'écriture du scan (position des lettres
    /// estimée d'après la largeur de l'encre) ; texte retapé : sur le texte écrit par l'application.
    /// </summary>
    public static List<StrikeSegment> StrikeSegments(PrintedLine line, Field f, double baseSize, Func<string, double, double> measure)
    {
        var output = new List<StrikeSegment>();
        if (f.Strikes.Count == 0 || f.Locked) return output;
        void Add(double x0, double x1, double baseline, double size, string color)
        {
            if (x1 - x0 < 0.5) return;
            output.Add(new StrikeSegment(x0, x1, baseline - 0.28 * size, Math.Max(1.5, 0.06 * size), color));
        }
        if (f.Text == f.OriginalText && !f.Ink.IsEmpty && !f.Text.Contains('\n'))
        {
            double size = baseSize, total = measure(f.Text, size);
            double k = total > 0 ? f.Ink.Width / total : 1;
            double baseline = f.Ink.Bottom - (f.Text.IndexOfAny("gjpqyç,;()".ToCharArray()) >= 0 ? 0.21 * size : 0);
            foreach (var s in f.Strikes)
                Add(f.Ink.X + measure(f.Text[..s.Start], size) * k, f.Ink.X + measure(f.Text[..(s.Start + s.Length)], size) * k, baseline, size, s.Color);
            return output;
        }
        var (sz, lines, top) = Layout(line, f, f.Text, baseSize, measure);
        int cursor = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i];
            int at = f.Text.IndexOf(l, cursor, StringComparison.Ordinal);
            int start = at < 0 ? cursor : at;
            cursor = start + l.Length;
            double x = LineX(f, measure(l, sz));
            double baseline = top + i * LineHeightRatio * sz + 0.91 * sz;
            foreach (var s in f.Strikes)
            {
                int a = Math.Max(s.Start, start), e = Math.Min(s.Start + s.Length, start + l.Length);
                if (e <= a) continue;
                Add(x + measure(l[..(a - start)], sz), x + measure(l[..(e - start)], sz), baseline, sz, s.Color);
            }
        }
        return output;
    }

    /// <summary>Couleur du trait en RVB.</summary>
    public static (byte R, byte G, byte B) Rgb(string color) => color == Strikes.Black ? ((byte)0, (byte)0, (byte)0) : ((byte)212, (byte)0, (byte)0);
}
