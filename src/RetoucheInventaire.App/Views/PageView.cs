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
    private WriteableBitmap? _bitmap;
    private Image? _background;
    private Point? _dragStart;

    /// <summary>Image redressée d'origine (null pour les pages TXT/CSV/XLSX).</summary>
    public Raster? Image => _image;

    public ModelPage Page { get; }
    public Grid Root { get; }

    /// <summary>Ce qui est affiché : la colonne des lots à gauche, puis la page encadrée.</summary>
    public Grid Host { get; }

    /// <summary>Colonne des numéros de lot, en dehors de la page : la mise en page du document n'est pas touchée.</summary>
    private readonly Canvas _gutter = new() { Width = GutterWidth };
    public const double GutterWidth = 84;
    private readonly Canvas _lotLayer = new();
    private readonly Dictionary<PrintedLine, TextBox> _lotBoxes = [];
    private readonly Func<bool> _lotsActive, _lotFrame;
    private static readonly SolidColorBrush TrashFill = new(ColorHelper.FromArgb(255, 253, 236, 236));
    private static readonly SolidColorBrush TrashInk = new(ColorHelper.FromArgb(255, 180, 40, 40));
    private static readonly SolidColorBrush AddInk = new(ColorHelper.FromArgb(255, 154, 163, 173));
    private static readonly SolidColorBrush LotBorder = new(ColorHelper.FromArgb(255, 201, 214, 230));

    public event Action<PageView, Field>? FieldFocused;
    public event Action<PageView>? Edited;
    public event Action<PageView>? Touched;
    public event Action<PageView>? LocksChanged;
    /// <summary>Numéro de lot tapé sur la page (ligne, valeur saisie).</summary>
    public event Action<PageView, PrintedLine, string>? LotChanged;
    /// <summary>Poubelle : cette ligne n'est pas un objet.</summary>
    public event Action<PageView, PrintedLine>? LotRemoved;
    /// <summary>« + » : ajouter un numéro sur cette ligne.</summary>
    public event Action<PageView, PrintedLine>? LotAdded;

    /// <param name="display">Image affichée (avec la colonne des lots tracée), sinon <paramref name="image"/>.</param>
    public PageView(ModelPage page, Raster? image, Raster? display, Func<bool> lotsActive, Func<bool> lotFrame)
    {
        Page = page;
        _image = image;
        _lotsActive = lotsActive;
        _lotFrame = lotFrame;
        Root = new Grid { Width = page.Width, Height = page.Height, Background = Paper, HorizontalAlignment = HorizontalAlignment.Center };
        if (image != null)
        {
            _bitmap = WinImaging.ToBitmap(display ?? image);
            _background = new Image { Source = _bitmap, Width = page.Width, Height = page.Height, Stretch = Stretch.Fill };
            Root.Children.Add(_background);
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
        Root.Children.Add(_lotLayer);
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
                foreach (var f in line.Cells.Where(c => !c.Locked)) AddField(f);
            }
        _overlayLayer.Children.Add(_focusFrame);
        _overlayLayer.Children.Add(_rubber);
        _overlayLayer.Children.Add(_loupe);
        RefreshLots();
    }

    /// <summary>Remplace l'image affichée (colonne des lots tracée ou non).</summary>
    public void SetDisplay(Raster display)
    {
        if (_background is null) return;
        _bitmap = WinImaging.ToBitmap(display);
        _background.Source = _bitmap;
    }

    /// <summary>
    /// Numéros de lot écrits sur la page, dans la colonne (modifiables au doigt), et colonne de gauche :
    /// type de ligne (T, Σ…), poubelle sur les lignes numérotées, « + » sur les autres.
    /// </summary>
    public void RefreshLots()
    {
        var focused = _lotBoxes.FirstOrDefault(p => p.Value.FocusState != FocusState.Unfocused).Key;
        _lotLayer.Children.Clear();
        _lotBoxes.Clear();
        var framed = _lotFrame() ? LotColumn.FramedTables(Page) : new List<Table>();
        var labels = Lots.Labels(Page);
        foreach (var t in Page.Tables)
            foreach (var line in t.Rows.SelectMany(r => r.Lines))
            {
                if (line.Lot.Length == 0) continue;
                var box = labels.First(l => l.Line == line).Box;
                AddLotBox(line, box, framed.Contains(t) || _image is null);
            }
        if (focused != null && _lotBoxes.TryGetValue(focused, out var again)) again.Focus(FocusState.Programmatic);
        RefreshGutter();
    }

    private void AddLotBox(PrintedLine line, RectD box, bool transparent)
    {
        var tb = new TextBox
        {
            Style = (Style)Application.Current.Resources["FieldBox"],
            Text = line.Lot,
            Width = Math.Max(1, box.Width),
            Height = Math.Max(1, box.Height),
            TextAlignment = TextAlignment.Center,
            Foreground = Ink,
            Background = transparent ? Clear : PaperOfBox(box),
        };
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
        tb.InputScope = scope;
        Canvas.SetLeft(tb, box.X);
        Canvas.SetTop(tb, box.Y);
        LayoutLot(tb, line, box);

        // Pendant la frappe, la suite se recale toute seule (petite pause après le dernier chiffre).
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        void Commit()
        {
            timer.Stop();
            if (tb.Text.Trim() != line.Lot) LotChanged?.Invoke(this, line, tb.Text);
        }
        timer.Tick += (_, _) => Commit();
        tb.TextChanged += (_, _) =>
        {
            timer.Stop();
            if (tb.Text.Trim().Length > 0 && tb.FocusState != FocusState.Unfocused) timer.Start();
        };
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Commit();
        };
        _lotBoxes[line] = tb;
        _lotLayer.Children.Add(tb);
    }

    /// <summary>Taille du numéro : celle du texte de l'objet, réduite si la colonne est étroite ; à la hauteur de la ligne.</summary>
    private void LayoutLot(TextBox tb, PrintedLine line, RectD box)
    {
        double size = Math.Max(5, Math.Min(BaseFontSize(line.Cells[0]), box.Height / LineHeightRatio));
        string text = line.Lot.Length == 0 ? " " : line.Lot;
        while (size > 5 && Measure(text, size) > box.Width - 2) size -= 0.5;
        tb.FontSize = size;
        double top = Math.Clamp(box.CenterY - box.Y - 0.58 * size, 0, Math.Max(0, box.Height - LineHeightRatio * size));
        tb.Padding = new Thickness(0, top, 0, 0);
    }

    /// <summary>Après un recalcul : met les nouveaux numéros en place sans quitter la case en cours de frappe.</summary>
    public void UpdateLots()
    {
        var labels = Lots.Labels(Page);
        bool same = labels.Count == _lotBoxes.Count && labels.All(l =>
            _lotBoxes.TryGetValue(l.Line, out var tb) && Math.Abs(Canvas.GetLeft(tb) - l.Box.X) < 0.5 && Math.Abs(tb.Width - Math.Max(1, l.Box.Width)) < 0.5);
        if (!same) { RefreshLots(); return; }
        foreach (var (line, box) in labels)
        {
            var tb = _lotBoxes[line];
            if (tb.FocusState == FocusState.Unfocused && tb.Text != line.Lot) tb.Text = line.Lot;
            LayoutLot(tb, line, box);
        }
        RefreshGutter();
    }

    public void RefreshGutter()
    {
        _gutter.Children.Clear();
        bool active = _lotsActive();
        foreach (var table in Page.Tables)
            foreach (var line in table.Rows.SelectMany(r => r.Lines))
            {
                if (line.OutsideTable || line.Cells.Count == 0) continue;
                AddBadge(line);
                if (!active) continue;
                bool has = line.Lot.Length > 0;
                if (!has && !Lots.CanAdd(line)) continue;
                double h = Math.Max(26, Math.Min(40, line.Bounds.Height + 6));
                var button = new Button
                {
                    Width = 42, Height = h, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                    CornerRadius = new CornerRadius(10),
                    Background = has ? TrashFill : Clear,
                    BorderBrush = has ? Clear : AddInk, BorderThickness = new Thickness(has ? 0 : 1),
                    Content = new FontIcon { Glyph = has ? "\uE74D" : "\uE710", FontSize = 16, Foreground = has ? TrashInk : AddInk },
                };
                ToolTipService.SetToolTip(button, has ? "Pas un objet : retirer ce numéro (les suivants se décalent)" : "Ajouter un numéro de lot sur cette ligne");
                Canvas.SetLeft(button, GutterWidth - 6 - 42);
                Canvas.SetTop(button, line.Bounds.CenterY - h / 2);
                var target = line;
                button.Click += (_, _) => { if (has) LotRemoved?.Invoke(this, target); else LotAdded?.Invoke(this, target); };
                _gutter.Children.Add(button);
            }
    }

    private SolidColorBrush PaperOfBox(RectD box)
    {
        if (_image is null) return Paper;
        var (r, g, b) = ImageExporter.PaperColor(_image, box);
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
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

    private void AddBadge(PrintedLine line)
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
        Canvas.SetLeft(badge, 6);
        Canvas.SetTop(badge, line.Bounds.CenterY - 12);
        _gutter.Children.Add(badge);
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
