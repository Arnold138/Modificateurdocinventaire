using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using RetoucheInventaire.App.Services;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Export;
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
    private readonly Dictionary<Field, SolidColorBrush> _paper = [];
    private readonly Border _loupe = new() { BorderBrush = Accent, BorderThickness = new Thickness(2), Background = Paper, Visibility = Visibility.Collapsed };
    private readonly Raster? _image;
    private readonly WriteableBitmap? _bitmap;
    private Point? _dragStart;

    /// <summary>Image redressée d'origine (null pour les pages TXT/CSV/XLSX).</summary>
    public Raster? Image => _image;

    public ModelPage Page { get; }
    public Grid Root { get; }

    /// <summary>Ce qui est affiché : la colonne des lots à gauche, puis la page encadrée.</summary>
    public Grid Host { get; }

    /// <summary>Colonne des numéros de lot, en dehors de la page : la mise en page du document n'est pas touchée.</summary>
    private readonly Canvas _gutter = new() { Width = GutterWidth };
    public const double GutterWidth = 72;
    private static readonly SolidColorBrush LotBorder = new(ColorHelper.FromArgb(255, 201, 214, 230));

    public event Action<PageView, Field>? FieldFocused;
    public event Action<PageView>? Edited;
    public event Action<PageView>? Touched;
    public event Action<PageView>? LocksChanged;
    /// <summary>Numéro de lot tapé dans la colonne de gauche (ligne, valeur saisie).</summary>
    public event Action<PageView, PrintedLine, string>? LotChanged;

    public PageView(ModelPage page, Raster? image)
    {
        Page = page;
        _image = image;
        Root = new Grid { Width = page.Width, Height = page.Height, Background = Paper, HorizontalAlignment = HorizontalAlignment.Center };
        if (image != null)
        {
            _bitmap = WinImaging.ToBitmap(image);
            Root.Children.Add(new Image { Source = _bitmap, Width = page.Width, Height = page.Height, Stretch = Stretch.Fill });
        }
        else
            Root.Children.Add(DrawGrid());
        _gutter.Height = page.Height;
        Host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 200, 200, 200)),
            BorderThickness = new Thickness(1),
            Child = Root,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(frame, 1);
        Host.Children.Add(_gutter);
        Host.Children.Add(frame);
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
        _overlayLayer.Children.Add(_loupe);
        RefreshLots();
    }

    /// <summary>Champs « Lot » de la colonne de gauche, à la hauteur de chaque objet.</summary>
    public void RefreshLots()
    {
        _gutter.Children.Clear();
        foreach (var (line, box) in Lots.Labels(Page))
        {
            double h = Math.Max(28, Math.Min(40, box.Height + 8));
            var tb = new TextBox
            {
                Text = line.Lot, Width = 58, Height = h, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2, 0, 2, 0),
                FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Accent, TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, BorderBrush = LotBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Background = Paper,
            };
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
            tb.InputScope = scope;
            Canvas.SetLeft(tb, GutterWidth - 8 - 58);
            Canvas.SetTop(tb, box.CenterY - h / 2);
            var target = line;
            bool done = false; // le champ est remplacé après validation : une seule fois suffit
            void Commit()
            {
                if (done || tb.Text.Trim() == target.Lot) return;
                done = true;
                LotChanged?.Invoke(this, target, tb.Text);
            }
            tb.LostFocus += (_, _) => Commit();
            tb.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Commit();
            };
            _gutter.Children.Add(tb);
        }
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

        tb.GotFocus += (_, _) => { ApplyVisual(tb, f, focused: true); ShowFrame(f); ShowLoupe(f); FieldFocused?.Invoke(this, f); };
        tb.LostFocus += (_, _) =>
        {
            ApplyVisual(tb, f, focused: false);
            _focusFrame.Visibility = Visibility.Collapsed;
            _loupe.Visibility = Visibility.Collapsed;
        };
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

    /// <summary>
    /// Case non modifiée : totalement invisible (opacité 0, mais toujours touchable),
    /// on ne voit que la photo d'origine, rien ne s'y superpose.
    /// Case modifiée ou en cours d'édition : fond couleur papier qui masque l'original, texte noir.
    /// </summary>
    private void ApplyVisual(TextBox tb, Field f, bool focused)
    {
        bool onImage = _image != null;
        bool show = focused || f.IsModified || !onImage;
        tb.Opacity = show ? 1 : 0;
        tb.Foreground = Ink;
        tb.Background = onImage ? PaperOf(f) : Clear;
    }

    private SolidColorBrush PaperOf(Field f)
    {
        if (_image is null) return Paper;
        if (!_paper.TryGetValue(f, out var brush))
        {
            var (r, g, b) = ImageExporter.PaperColor(_image, f.Box);
            _paper[f] = brush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        }
        return brush;
    }

    /// <summary>
    /// Pendant l'édition, l'original de la case est montré en loupe juste au-dessus :
    /// on compare avec ce que l'OCR a lu, sans texte superposé.
    /// </summary>
    private void ShowLoupe(Field f)
    {
        if (_bitmap is null) return;
        var b = RectD.FromEdges(Math.Max(0, f.Box.Left - 4), Math.Max(0, f.Box.Top - 2),
                                Math.Min(Page.Width, f.Box.Right + 4), Math.Min(Page.Height, f.Box.Bottom + 2));
        var holder = new Canvas { Width = b.Width, Height = b.Height, Clip = new RectangleGeometry { Rect = new Rect(0, 0, b.Width, b.Height) } };
        var img = new Image { Source = _bitmap, Width = Page.Width, Height = Page.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(img, -b.X);
        Canvas.SetTop(img, -b.Y);
        holder.Children.Add(img);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Original", FontSize = 11, Foreground = Accent, Margin = new Thickness(4, 0, 0, 1) });
        stack.Children.Add(holder);
        _loupe.Child = stack;

        const double scale = 1.4;
        _loupe.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        double height = (b.Height + 20) * scale;
        double top = b.Top - height - 6;
        if (top < 0) top = b.Bottom + 6;
        Canvas.SetLeft(_loupe, Math.Max(0, Math.Min(b.Left, Page.Width - (b.Width + 4) * scale)));
        Canvas.SetTop(_loupe, top);
        _loupe.Visibility = Visibility.Visible;
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
