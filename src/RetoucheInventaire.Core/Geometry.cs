using System.Text.Json.Serialization;

namespace RetoucheInventaire.Core;

/// <summary>Point en pixels de page.</summary>
public readonly record struct PointD(double X, double Y)
{
    public double DistanceTo(PointD o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

/// <summary>Rectangle en pixels de page (origine en haut à gauche).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Left => X;
    [JsonIgnore] public double Top => Y;
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    [JsonIgnore] public double CenterX => X + Width / 2;
    [JsonIgnore] public double CenterY => Y + Height / 2;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD Empty => default;

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public RectD Union(RectD o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return FromEdges(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool IntersectsWith(RectD o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;

    public RectD Inflate(double dx, double dy) => FromEdges(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public double VerticalOverlap(RectD o) => Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top));
}

/// <summary>Quadrilatère de recadrage (coins de la feuille dans l'image source).</summary>
public sealed record Quad(PointD TopLeft, PointD TopRight, PointD BottomRight, PointD BottomLeft)
{
    public static Quad FullImage(int width, int height) =>
        new(new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1));

    public PointD[] ToArray() => [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>Taille « naturelle » de la feuille redressée.</summary>
    public (int Width, int Height) NaturalSize()
    {
        double w = Math.Max(TopLeft.DistanceTo(TopRight), BottomLeft.DistanceTo(BottomRight));
        double h = Math.Max(TopLeft.DistanceTo(BottomLeft), TopRight.DistanceTo(BottomRight));
        return ((int)Math.Round(w) + 1, (int)Math.Round(h) + 1);
    }

    public double Area()
    {
        var p = ToArray();
        double a = 0;
        for (int i = 0; i < 4; i++)
        {
            var q = p[(i + 1) % 4];
            a += p[i].X * q.Y - q.X * p[i].Y;
        }
        return Math.Abs(a) / 2;
    }
}
