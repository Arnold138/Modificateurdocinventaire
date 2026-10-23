using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RetoucheInventaire.App.Services;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;
using Windows.Foundation;
using Windows.System;
using ModelPage = RetoucheInventaire.Core.Model.Page;

namespace RetoucheInventaire.App.Views;

/// <summary>
/// Une page à l'écran : l'image d'origine et, par-dessus, un champ par case, à sa position exacte.
/// Tout est placé en absolu (pixels de page) : modifier un texte ne peut rien déplacer d'autre.
/// Un champ non modifié est invisible (on voit l'original) ; un champ modifié masque l'original
/// dans sa case et affiche le nouveau texte en noir, réduit si besoin pour ne jamais en sortir.
/// </summary>
public sealed class PageView
{
    private static readonly SolidColorBrush Clear = new(Colors.Transparent);
    private static readonly SolidColorBrush Paper = new(Colors.White);
    private static readonly SolidColorBrush Ink = new(Colors.Black);
    private static readonly SolidColorBrush Accent = new(ColorHelper.FromArgb(255, 0, 103, 192));
    private static readonly SolidColorBrush LockFill = new(ColorHelper.FromArgb(48, 255, 140, 0));
    private static readonly SolidColorBrush LockStroke = new(ColorHelper.FromArgb(255, 220, 110, 0));
    private static readonly TextBlock Meter = new() { FontFamily = new FontFamily("Times New Roman") };

    private const double LineHeightRatio = 1.15; // hauteur de ligne de Times New Roman, en em

    private readonly Canvas _fieldLayer = new();
    private readonly Canvas _overlayLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _inputLayer = new() { IsHitTestVisible = false };
    private readonly Rectangle _focusFrame = new() { Stroke = Accent, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Rectangle _rubber = new() { Stroke = LockStroke, StrokeThickness = 2, Fill = LockFill, Visibility = Visibility.Collapsed };
    private readonly Dictionary<Field, TextBox> _boxes = [];
    private readonly Dictionary<Field, double> _baseSize = [];
    private Point? _dragStart;

    public ModelPage Page { get; }
    public Grid Root { get; }

    public event Action<PageView, Field>? FieldFocused;
    public event Action<PageView>? Edited;
    public event Action<PageView>? Touched;
    public event Action<PageView>? LocksChanged;

    public PageView(ModelPage page, Raster? image)
    {
        Page = page;
        Root = new Grid { Width = page.Width, Height = page.Height, Background = Paper, HorizontalAlignment = HorizontalAlignment.Center };
        if (image != null)
            Root.Children.Add(new Image { Source = WinImaging.ToBitmap(image), Width = page.Width, Height = page.Height, Stretch = Stretch.Fill });
        else
            Root.Children.Add(DrawGrid());
        Root.Children.Add(_fieldLayer);
        Root.Children.Add(_overlayLayer);
        Root.Children.Add(_inputLayer);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Touched?.Invoke(this)), handledEventsToo: true);

        _inputLayer.PointerPressed += OnLockPressed;
        _inputLayer.PointerMoved += OnLockMoved;
        _inputLayer.PointerReleased += OnLockReleased;
        _inputLayer.PointerCanceled += (_, _) => { _dragStart = null; _rubber.Visibility = Visibility.Collapsed; };

        Build();
    }

    /// <summary>Reconstruit les champs et repères (après un verrouillage ou un changement de type de ligne).</summary>
    public void Refresh()
    {
        _fieldLayer.Children.Clear();
        _overlayLayer.Children.Clear();
        _boxes.Clear();
        _baseSize.Clear();
        Build();
    }

    public void SetLockMode(bool on)
    {
        _inputLayer.Background = on ? Clear : null;
        _inputLayer.IsHitTestVisible = on;
    }

    public void Revert(Field field)
    {
        if (_boxes.TryGetValue(field, out var tb)) tb.Text = field.OriginalText;
        else field.Text = field.OriginalText;
    }

    public void Focus(Field field)
    {
        if (_boxes.TryGetValue(field, out var tb)) tb.Focus(FocusState.Programmatic);
    }

    private void Build()
    {
        foreach (var zone in Page.LockZones) AddLockZone(zone);
        foreach (var table in Page.Tables)
            foreach (var line in table.Rows.SelectMany(r => r.Lines))
            {
                AddBadge(table, line);
                foreach (var f in line.Cells.Where(c => !c.Locked)) AddField(f);
            }
        _overlayLayer.Children.Add(_focusFrame);
        _overlayLayer.Children.Add(_rubber);
    }

    // ---------- Champs ----------

    private void AddField(Field f)
    {
        var tb = new TextBox
        {
            Style = (Style)Application.Current.Resources["FieldBox"],
            Text = f.Text,
            Width = Math.Max(1, f.Box.Width),
            Height = Math.Max(1, f.Box.Height),
            TextAlignment = f.Align switch { TextAlign.Right => TextAlignment.Right, TextAlign.Center => TextAlignment.Center, _ => TextAlignment.Left },
        };
        if (f.Column > 0 && (f.OriginalText.Length == 0 || f.OriginalText.Any(char.IsDigit)))
        {
            // Clavier tactile numérique pour les colonnes de valeurs (les lettres restent accessibles).
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
            tb.InputScope = scope;
        }
        Canvas.SetLeft(tb, f.Box.X);
        Canvas.SetTop(tb, f.Box.Y);

        _baseSize[f] = BaseFontSize(f);
        Layout(tb, f);
        ApplyVisual(tb, f, focused: false);

        tb.GotFocus += (_, _) => { ApplyVisual(tb, f, focused: true); ShowFrame(f); FieldFocused?.Invoke(this, f); };
        tb.LostFocus += (_, _) => { ApplyVisual(tb, f, focused: false); _focusFrame.Visibility = Visibility.Collapsed; };
        tb.TextChanged += (_, _) =>
        {
            if (f.Text == tb.Text) return;
            f.Text = tb.Text;
            Layout(tb, f);
            ApplyVisual(tb, f, tb.FocusState != FocusState.Unfocused);
            Edited?.Invoke(this);
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            FocusNextInColumn(f);
        };

        _boxes[f] = tb;
        _fieldLayer.Children.Add(tb);
    }

    /// <summary>
    /// Taille de police de référence : celle qui redonne la largeur du texte imprimé
    /// (plus fiable que la hauteur des boîtes OCR), bornée autour de l'estimation du cœur.
    /// </summary>
    private static double BaseFontSize(Field f)
    {
        double size = f.FontSize;
        if (!f.Ink.IsEmpty && f.OriginalText.Length > 0)
        {
            double w100 = Measure(f.OriginalText, 100);
            if (w100 > 0) size = Math.Clamp(100 * f.Ink.Width / w100, 0.7 * f.FontSize, 1.3 * f.FontSize);
        }
        return Math.Max(5, Math.Min(size, f.Box.Height / LineHeightRatio));
    }

    /// <summary>Réduit la police jusqu'à ce que le texte tienne dans sa case, et le place à la hauteur de l'original.</summary>
    private void Layout(TextBox tb, Field f)
    {
        double size = _baseSize[f];
        double avail = Math.Max(4, f.Box.Width - f.Indent - 3);
        string text = tb.Text.Length == 0 ? " " : tb.Text;
        while (size > 5 && Measure(text, size) > avail) size -= 0.5;
        tb.FontSize = size;

        double center = f.Ink.IsEmpty ? f.Box.CenterY : f.Ink.CenterY;
        double top = Math.Clamp(center - f.Box.Y - 0.58 * size, 0, Math.Max(0, f.Box.Height - LineHeightRatio * size));
        tb.Padding = f.Align switch
        {
            TextAlign.Right => new Thickness(0, top, f.Indent, 0),
            TextAlign.Center => new Thickness(0, top, 0, 0),
            _ => new Thickness(f.Indent, top, 0, 0),
        };
    }

    private static double Measure(string s, double size)
    {
        Meter.FontSize = size;
        Meter.Text = s;
        Meter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Meter.DesiredSize.Width;
    }

    private void ApplyVisual(TextBox tb, Field f, bool focused)
    {
        bool onImage = Page.Source == PageSource.Image;
        bool show = focused || f.IsModified || !onImage;
        tb.Foreground = show ? Ink : Clear; // texte modifié en noir, comme l'original
        tb.Background = onImage && show ? Paper : Clear;
    }

    private void ShowFrame(Field f)
    {
        _focusFrame.Width = f.Box.Width + 4;
        _focusFrame.Height = f.Box.Height + 4;
        Canvas.SetLeft(_focusFrame, f.Box.X - 2);
        Canvas.SetTop(_focusFrame, f.Box.Y - 2);
        _focusFrame.Visibility = Visibility.Visible;
    }

    /// <summary>Entrée : passe à la case suivante de la même colonne (saisie d'une colonne de valeurs).</summary>
    private void FocusNextInColumn(Field f)
    {
        var column = Page.Tables.SelectMany(t => t.Rows).SelectMany(r => r.Lines).SelectMany(l => l.Cells)
            .Where(c => c.Column == f.Column && _boxes.ContainsKey(c)).ToList();
        int i = column.IndexOf(f);
        if (i >= 0 && i + 1 < column.Count) _boxes[column[i + 1]].Focus(FocusState.Programmatic);
    }

    // ---------- Repères : type de ligne, zones verrouillées ----------

    private void AddBadge(Table table, PrintedLine line)
    {
        if (line.Kind == LineKind.Article) return;
        var (letter, color) = line.Kind switch
        {
            LineKind.Titre => ("T", ColorHelper.FromArgb(255, 0, 103, 192)),
            LineKind.Total => ("Σ", ColorHelper.FromArgb(255, 16, 124, 16)),
            LineKind.EnTete => ("E", ColorHelper.FromArgb(255, 96, 96, 96)),
            _ => ("×", ColorHelper.FromArgb(255, 180, 40, 40)),
        };
        var badge = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(color),
            Child = new TextBlock
            {
                Text = letter, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Paper,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, Math.Max(2, table.Bounds.Left - 30));
        Canvas.SetTop(badge, line.Bounds.CenterY - 12);
        _overlayLayer.Children.Add(badge);
    }

    private void AddLockZone(RectD z)
    {
        var rect = new Rectangle
        {
            Width = z.Width, Height = z.Height, Fill = LockFill, Stroke = LockStroke, StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        Canvas.SetLeft(rect, z.X);
        Canvas.SetTop(rect, z.Y);
        _overlayLayer.Children.Add(rect);
        var icon = new FontIcon { Glyph = "", FontSize = 18, Foreground = LockStroke };
        Canvas.SetLeft(icon, z.X + 4);
        Canvas.SetTop(icon, z.Y + 4);
        _overlayLayer.Children.Add(icon);
    }

    private Canvas DrawGrid()
    {
        var c = new Canvas();
        foreach (var t in Page.Tables)
        {
            double top = t.RowYs[0], bottom = t.RowYs[^1], left = t.ColumnXs[0], right = t.ColumnXs[^1];
            foreach (var x in t.ColumnXs) c.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = bottom, Stroke = Ink, StrokeThickness = 1.5 });
            foreach (var y in t.RowYs) c.Children.Add(new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = Ink, StrokeThickness = 1.5 });
        }
        return c;
    }

    // ---------- Mode verrouillage : tracer une zone au doigt, toucher une zone pour la retirer ----------

    private void OnLockPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(_inputLayer).Position;
        _inputLayer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnLockMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not Point a) return;
        var b = e.GetCurrentPoint(_inputLayer).Position;
        _rubber.Width = Math.Abs(b.X - a.X);
        _rubber.Height = Math.Abs(b.Y - a.Y);
        Canvas.SetLeft(_rubber, Math.Min(a.X, b.X));
        Canvas.SetTop(_rubber, Math.Min(a.Y, b.Y));
        _rubber.Visibility = Visibility.Visible;
    }

    private void OnLockReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not Point a) return;
        _dragStart = null;
        _inputLayer.ReleasePointerCapture(e.Pointer);
        _rubber.Visibility = Visibility.Collapsed;

        var b = e.GetCurrentPoint(_inputLayer).Position;
        var zone = RectD.FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        bool changed;
        if (zone.Width < 8 && zone.Height < 8)
            changed = PageEdits.RemoveLockZoneAt(Page, b.X, b.Y);
        else
        {
            int before = Page.LockZones.Count;
            PageEdits.AddLockZone(Page, zone);
            changed = Page.LockZones.Count != before;
        }
        if (!changed) return;
        Refresh();
        LocksChanged?.Invoke(this);
    }
}
