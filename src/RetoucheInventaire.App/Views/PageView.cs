using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
/// Une page à l'écran : l'image (recomposée si des rangées ont bougé) et, par-dessus, un champ par case, à sa place.
/// Les cases sont placées d'après la mise en page (<see cref="Arrangement"/>) : un lot 0 recollé après le dernier lot
/// s'édite là où il est affiché. Une case non modifiée est invisible (on voit l'original) ; une case modifiée masque
/// l'original et affiche le nouveau texte (noir ou en couleur), à la taille de l'imprimé.
/// </summary>
public sealed class PageView
{
    private static readonly SolidColorBrush Clear = new(Colors.Transparent);
    private static readonly SolidColorBrush Paper = new(Colors.White);
    private static readonly SolidColorBrush Ink = new(Colors.Black);
    private static readonly SolidColorBrush Accent = new(ColorHelper.FromArgb(255, 0, 103, 192));
    private static readonly SolidColorBrush LockFill = new(ColorHelper.FromArgb(48, 255, 140, 0));
    private static readonly SolidColorBrush LockStroke = new(ColorHelper.FromArgb(255, 220, 110, 0));
    private static readonly SolidColorBrush TrashFill = new(ColorHelper.FromArgb(255, 253, 236, 236));
    private static readonly SolidColorBrush TrashInk = new(ColorHelper.FromArgb(255, 180, 40, 40));
    private static readonly SolidColorBrush AddInk = new(ColorHelper.FromArgb(255, 154, 163, 173));
    private static readonly SolidColorBrush GripInk = new(ColorHelper.FromArgb(255, 74, 85, 96));
    private static readonly SolidColorBrush GripFill = new(ColorHelper.FromArgb(255, 238, 241, 244));
    private static readonly SolidColorBrush GripBorder = new(ColorHelper.FromArgb(255, 205, 211, 218));
    private static readonly TextBlock Meter = new() { FontFamily = new FontFamily("Times New Roman") };

    private const double LineHeightRatio = 1.15; // hauteur de ligne de Times New Roman, en em
    public const double GutterWidth = 112;

    private readonly Canvas _fieldLayer = new();
    private readonly Canvas _colorLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _lotLayer = new();
    private readonly Canvas _strikeLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _overlayLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _inputLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _gutter = new() { Width = GutterWidth };
    private readonly Rectangle _focusFrame = new() { Stroke = Accent, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Rectangle _rubber = new() { Stroke = LockStroke, StrokeThickness = 2, Fill = LockFill, Visibility = Visibility.Collapsed };
    private readonly Rectangle _dropLine = new() { Height = 4, Fill = Accent, RadiusX = 2, RadiusY = 2, Visibility = Visibility.Collapsed };
    private readonly Rectangle _ghost = new() { Stroke = Accent, StrokeThickness = 2, Fill = new SolidColorBrush(ColorHelper.FromArgb(26, 10, 102, 194)), Visibility = Visibility.Collapsed };
    private readonly Border _loupe = new() { BorderBrush = Accent, BorderThickness = new Thickness(2), Background = Paper, Visibility = Visibility.Collapsed };
    private readonly Dictionary<Field, TextBox> _boxes = [];
    private readonly Dictionary<Field, TextBlock> _colorTexts = [];
    private readonly Dictionary<Field, Brush> _paper = [];
    private readonly Dictionary<PrintedLine, TextBox> _lotBoxes = [];
    private readonly List<UIElement> _lockMarks = [];
    private readonly Func<bool> _lotsActive;
    private readonly Func<int, int?> _mapY, _unmapY;
    private Dictionary<Field, (Arrangement.ViewCell Cell, Arrangement.ViewLine Entry)> _place = [];
    private Arrangement.ViewPage _view;
    private Raster? _image;
    private Canvas? _grid;
    private WriteableBitmap? _bitmap;
    private Image? _background;
    private Point? _dragStart;
    private (Field Field, int Start, int End)? _selection;

    /// <summary>Image affichée (null pour les pages TXT/CSV/XLSX).</summary>
    public Raster? Image => _image;
    /// <summary>La page du projet affichée.</summary>
    public ModelPage Page => _view.Page;
    public Grid Root { get; }
    /// <summary>Ce qui est affiché : la colonne de gauche (poignées, lots), puis la page encadrée.</summary>
    public Grid Host { get; }

    public event Action<PageView, Field>? FieldFocused;
    public event Action<PageView, Field>? Edited;
    /// <summary>Juste avant la première modification d'une case (pour le retour en arrière).</summary>
    public event Action<PageView, Field>? BeforeEdit;
    /// <summary>Juste avant un changement des zones verrouillées.</summary>
    public event Action<PageView>? BeforeChange;
    public event Action<PageView>? LocksChanged;
    /// <summary>Numéro de lot tapé sur la page (ligne, valeur saisie).</summary>
    public event Action<PageView, PrintedLine, string>? LotChanged;
    /// <summary>Poubelle : cette ligne n'est pas un objet.</summary>
    public event Action<PageView, PrintedLine>? LotRemoved;
    /// <summary>« + » : ajouter une case de lot sur cette ligne.</summary>
    public event Action<PageView, PrintedLine>? LotAdded;
    /// <summary>Rangée glissée : (tableau, rangée déplacée, rangée devant laquelle la poser ou null pour la fin).</summary>
    public event Action<PageView, Table, int, int?>? RowMoved;
    /// <summary>Poignée d'une ligne dans un bloc lâchée : (tableau, ligne, rangée avant laquelle la poser ou null : à la fin).</summary>
    public event Action<PageView, Table, PrintedLine, int?>? LineMoved;
    /// <summary>Poignée d'un lot 0 recollé lâchée : (tableau, lot 0, rangée avant laquelle le poser ou null : à la fin du tableau).</summary>
    public event Action<PageView, Table, Arrangement.Unit, int?>? UnitMoved;

    /// <summary>Une action du menu « Ligne » (clic droit, appui long ou bouton « Ligne ») : dupliquer, séparer…</summary>
    public sealed record LineCommand(string Label, string Hint, string Glyph, Action Run, bool Danger = false);

    /// <summary>Actions proposées pour une case, ajoutées en tête du menu Couper / Copier / Coller de la case.</summary>
    public Func<Field, IReadOnlyList<LineCommand>>? LineCommands { get; set; }

    private const string LineTag = "ligne";
    /// <summary>Menu de toutes les cases de la page (Couper, Copier, Coller… et les actions « Ligne »).</summary>
    private readonly TextCommandBarFlyout _textMenu = new() { AlwaysExpanded = true };
    /// <summary>Case appuyée au bouton droit (la barre de la case qui apparaît peut décaler la page sous le pointeur).</summary>
    private (Field Field, long At)? _rightPress;

    public PageView(Arrangement.ViewPage view, Raster? display, Func<bool> lotsActive, Func<int, int?> mapY, Func<int, int?> unmapY)
    {
        _view = view;
        _image = display;
        _lotsActive = lotsActive;
        _mapY = mapY;
        _unmapY = unmapY;
        SetPlacement(view);
        Root = new Grid { Width = view.Width, Height = view.Height, Background = Paper, HorizontalAlignment = HorizontalAlignment.Center };
        if (display != null)
        {
            _bitmap = WinImaging.ToBitmap(display);
            _background = new Image { Source = _bitmap, Width = view.Width, Height = view.Height, Stretch = Stretch.Fill };
            Root.Children.Add(_background);
        }
        else
            Root.Children.Add(_grid = DrawGrid());
        _gutter.Height = view.Height;
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
        Root.Children.Add(_colorLayer);
        Root.Children.Add(_lotLayer);
        Root.Children.Add(_strikeLayer);
        Root.Children.Add(_overlayLayer);
        Root.Children.Add(_inputLayer);

        _textMenu.Opening += OnTextMenuOpening;
        _inputLayer.PointerPressed += OnLockPressed;
        _inputLayer.PointerMoved += OnLockMoved;
        _inputLayer.PointerReleased += OnLockReleased;
        _inputLayer.PointerCanceled += (_, _) => { _dragStart = null; _rubber.Visibility = Visibility.Collapsed; };

        Build();
    }

    private void SetPlacement(Arrangement.ViewPage view)
    {
        _view = view;
        _place = [];
        foreach (var entry in view.Lines)
            foreach (var cell in entry.Cells) _place[cell.Field] = (cell, entry);
    }

    /// <summary>Reconstruit les champs et repères (après un verrouillage ou un changement de type de ligne).</summary>
    public void Refresh()
    {
        _fieldLayer.Children.Clear();
        _colorLayer.Children.Clear();
        _overlayLayer.Children.Clear();
        _lockMarks.Clear();
        _boxes.Clear();
        _colorTexts.Clear();
        _paper.Clear();
        Build();
    }

    public void SetLockMode(bool on)
    {
        _inputLayer.Background = on ? Clear : null;
        _inputLayer.IsHitTestVisible = on;
    }

    public void Revert(Field field)
    {
        if (_boxes.TryGetValue(field, out var tb)) tb.Text = ToBox(field.OriginalText);
        else
        {
            Strikes.Adjust(field, field.Text, field.OriginalText);
            field.Text = field.OriginalText;
        }
        RefreshStrikes();
        RefreshColors();
    }

    public void Focus(Field field)
    {
        if (_boxes.TryGetValue(field, out var tb)) tb.Focus(FocusState.Programmatic);
    }

    public bool Shows(Field field) => _boxes.ContainsKey(field);

    public PrintedLine? LineOf(Field f) => _place.TryGetValue(f, out var p) ? p.Entry.Line : null;

    /// <summary>Ligne affichée d'une case (avec sa page d'origine : un lot 0 vient peut-être d'une autre page).</summary>
    public Arrangement.ViewLine? EntryOf(Field f) => _place.TryGetValue(f, out var p) ? p.Entry : null;

    /// <summary>Morceau sélectionné dans la case (vide : toute la case).</summary>
    public (int Start, int End) SelectionFor(Field f) =>
        _selection is { } s && s.Field == f ? (s.Start, s.End) : (0, 0);

    /// <summary>Remplace l'image affichée.</summary>
    public void SetDisplay(Raster display)
    {
        _image = display;
        _paper.Clear();
        if (_background is null) return;
        _bitmap = WinImaging.ToBitmap(display);
        _background.Source = _bitmap;
        _background.Height = display.Height;
    }

    private void Build()
    {
        DrawLockZones();
        foreach (var (f, p) in _place)
            if (!f.Locked) AddField(f, p.Cell);
        _overlayLayer.Children.Add(_focusFrame);
        _overlayLayer.Children.Add(_rubber);
        _overlayLayer.Children.Add(_ghost);
        _overlayLayer.Children.Add(_dropLine);
        _overlayLayer.Children.Add(_loupe);
        RefreshLots();
        RefreshStrikes();
        RefreshColors();
    }

    /// <summary>
    /// Nouvelle mise en page (rangée agrandie, total recalculé…) : tout est replacé sans quitter la case en cours.
    /// Si d'autres cases apparaissent ou disparaissent, la page est reconstruite.
    /// </summary>
    public void Relayout(Arrangement.ViewPage view, Raster? display)
    {
        var before = _place.Keys.ToHashSet();
        SetPlacement(view);
        bool same = before.SetEquals(_place.Keys);
        Root.Width = view.Width;
        Root.Height = view.Height;
        _gutter.Height = view.Height;
        if (display != null) SetDisplay(display);
        if (_grid != null)
        {
            int i = Root.Children.IndexOf(_grid);
            _grid = DrawGrid();
            if (i >= 0) Root.Children[i] = _grid;
        }
        _paper.Clear();
        if (!same) { Refresh(); return; }
        foreach (var (f, tb) in _boxes)
        {
            Layout(tb, f);
            ApplyVisual(tb, f, tb.FocusState != FocusState.Unfocused);
        }
        DrawLockZones();
        UpdateLots();
        RefreshStrikes();
        RefreshColors();
    }

    /// <summary>Textes changés ailleurs (totaux recalculés) : les cases affichées suivent.</summary>
    public void SyncFields(IEnumerable<Field> fields)
    {
        foreach (var f in fields)
        {
            if (!_boxes.TryGetValue(f, out var tb) || tb.FocusState != FocusState.Unfocused) continue;
            if (FromBox(tb.Text) != f.Text) tb.Text = ToBox(f.Text);
            Layout(tb, f);
            ApplyVisual(tb, f, false);
        }
        RefreshStrikes();
        RefreshColors();
    }

    // ---------- Zones verrouillées ----------

    private void DrawLockZones()
    {
        foreach (var mark in _lockMarks) _overlayLayer.Children.Remove(mark);
        _lockMarks.Clear();
        foreach (var zone in Page.LockZones)
        {
            int? y = _mapY((int)Math.Round(zone.Y));
            if (y is null) continue;
            AddLockZone(zone with { Y = y.Value });
        }
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
        _overlayLayer.Children.Insert(0, rect);
        _lockMarks.Add(rect);
        var icon = new FontIcon { Glyph = "", FontSize = 18, Foreground = LockStroke };
        Canvas.SetLeft(icon, z.X + 4);
        Canvas.SetTop(icon, z.Y + 4);
        _overlayLayer.Children.Insert(0, icon);
        _lockMarks.Add(icon);
    }

    // ---------- Numéros de lot ----------

    /// <summary>Numéros écrits sur la page, dans la colonne (modifiables au doigt), puis la colonne de gauche.</summary>
    public void RefreshLots()
    {
        var focused = _lotBoxes.FirstOrDefault(p => p.Value.FocusState != FocusState.Unfocused).Key;
        _lotLayer.Children.Clear();
        _lotBoxes.Clear();
        foreach (var entry in _view.Lines)
            if (entry.LotBox is RectD box) AddLotBox(entry.Line, box);
        if (focused != null && _lotBoxes.TryGetValue(focused, out var again)) again.Focus(FocusState.Programmatic);
        RefreshGutter();
    }

    private void AddLotBox(PrintedLine line, RectD box)
    {
        var tb = new TextBox
        {
            Style = (Style)Application.Current.Resources["FieldBox"],
            Text = line.Lot,
            Width = Math.Max(1, box.Width),
            Height = Math.Max(1, box.Height),
            TextAlignment = TextAlignment.Center,
            Foreground = Ink,
            Background = Clear,
        };
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
        tb.InputScope = scope;
        Canvas.SetLeft(tb, box.X);
        Canvas.SetTop(tb, box.Y);
        LayoutLot(tb, line, box);

        // Le numéro est enregistré quand on quitte la case (Entrée ou toucher ailleurs) ; rien d'autre ne change.
        void Commit()
        {
            if (tb.Text.Trim() != line.Lot) LotChanged?.Invoke(this, line, tb.Text);
        }
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
        var c0 = line.Cells[0];
        double size = Math.Max(5, Math.Min(BaseFontSize(c0, c0.Box.Height - line.Extra, false), box.Height / LineHeightRatio));
        string text = line.Lot.Length == 0 ? " " : line.Lot;
        while (size > 5 && Measure(text, size, false) > box.Width - 2) size -= 0.5;
        tb.FontSize = size;
        double top = Math.Clamp(box.CenterY - box.Y - 0.58 * size, 0, Math.Max(0, box.Height - LineHeightRatio * size));
        tb.Padding = new Thickness(0, top, 0, 0);
    }

    /// <summary>Après un changement : met les numéros en place sans quitter la case en cours de frappe.</summary>
    public void UpdateLots()
    {
        var boxes = _view.Lines.Where(e => e.LotBox != null).ToList();
        bool same = boxes.Count == _lotBoxes.Count && boxes.All(e => _lotBoxes.ContainsKey(e.Line));
        if (!same) { RefreshLots(); return; }
        foreach (var e in boxes)
        {
            var tb = _lotBoxes[e.Line];
            var box = e.LotBox!.Value;
            if (tb.FocusState == FocusState.Unfocused && tb.Text != e.Line.Lot) tb.Text = e.Line.Lot;
            Canvas.SetLeft(tb, box.X);
            Canvas.SetTop(tb, box.Y);
            tb.Width = Math.Max(1, box.Width);
            tb.Height = Math.Max(1, box.Height);
            LayoutLot(tb, e.Line, box);
        }
        RefreshGutter();
    }

    /// <summary>Repère de hauteur d'une ligne à l'écran : sa 1re ligne de texte.</summary>
    private static RectD? LineBand(Arrangement.ViewLine entry)
    {
        if (entry.Cells.Count == 0) return null;
        var c = entry.Cells[0];
        var b = PageLayout.BaseBox(entry.Line, c.Field);
        return b with { X = b.X + c.Dx, Y = b.Y + c.Dy };
    }

    /// <summary>Mode « Modifier » : poignées ⋮⋮ affichées (la poubelle et le « + » des lots sont masqués pendant ce temps).</summary>
    public bool MoveMode { get; private set; }

    public void SetMoveMode(bool on)
    {
        MoveMode = on;
        RefreshGutter();
    }

    /// <summary>
    /// Colonne de gauche : type de ligne (T, Σ…) ; hors « Modifier » : poubelle sur les lignes numérotées, « + » sur les autres ;
    /// en « Modifier » : les poignées ⋮⋮ seules (rien d'autre à toucher à côté).
    /// </summary>
    public void RefreshGutter()
    {
        _gutter.Children.Clear();
        bool active = _lotsActive() && !MoveMode;
        foreach (var entry in _view.Lines)
        {
            var line = entry.Line;
            if (line.OutsideTable || entry.CarriedOnly || line.Cells.Count == 0) continue;
            if (LineBand(entry) is not RectD b) continue;
            AddBadge(line, b.CenterY);
            if (!active || entry.Table.LotsOff) continue;
            bool has = Lots.HasCell(line);
            if (!has && !Lots.CanAdd(line, entry.Table.Rows.FirstOrDefault(r => r.Lines.Contains(line)))) continue;
            double h = Math.Max(26, Math.Min(40, b.Height + 6));
            var button = new Button
            {
                Width = 42, Height = h, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                CornerRadius = new CornerRadius(10),
                Background = has ? TrashFill : Clear,
                BorderBrush = has ? Clear : AddInk, BorderThickness = new Thickness(has ? 0 : 1),
                Content = new FontIcon { Glyph = has ? "" : "", FontSize = 16, Foreground = has ? TrashInk : AddInk },
            };
            ToolTipService.SetToolTip(button, has ? "Pas un objet : retirer cette case" : "Ajouter une case de lot sur cette ligne");
            Canvas.SetLeft(button, GutterWidth - 6 - 42);
            Canvas.SetTop(button, b.CenterY - h / 2);
            var target = line;
            button.Click += (_, _) => { if (has) LotRemoved?.Invoke(this, target); else LotAdded?.Invoke(this, target); };
            _gutter.Children.Add(button);
        }
        DrawGrips();
    }

    private void AddBadge(PrintedLine line, double centerY)
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
        Canvas.SetLeft(badge, 4);
        Canvas.SetTop(badge, centerY - 12);
        _gutter.Children.Add(badge);
    }

    // ---------- Déplacer une rangée (poignée ⋮⋮ juste à gauche de la poubelle) ----------

    private void DrawGrips()
    {
        if (!MoveMode) return;
        foreach (var vt in _view.Tables)
        {
            // Lots 0 recollés ici (fin de partie ou endroit choisi) : leur poignée les replace où on veut dans ce tableau.
            foreach (var u in vt.Units ?? [])
                AddGrip(vt, new Arrangement.Strip(-1, u.Top, u.Bottom), null, (u.Top + u.Bottom) / 2, Math.Max(24, Math.Min(40, u.Bottom - u.Top - 4)),
                    "Lot 0 : glisser pour le placer ailleurs dans ce tableau", u.Unit);
            if (vt.Strips.Count < 2) continue;
            foreach (var strip in vt.Strips)
            {
                var row = vt.Table.Rows[strip.R];
                if (row.Lines.Count == 0 || row.Lines.All(l => l.Kind == LineKind.EnTete) || row.Lines.Any(RetoucheInventaire.Core.Analysis.LineClassifier.LooksLikeTotal)) continue; // ni en-tête ni total
                if (row.Lines.All(l => l.Cells.All(c => string.IsNullOrWhiteSpace(c.Text)))) continue;
                // Bloc de plusieurs lignes : la poignée de la 1re ligne déplace le bloc, celle de chaque autre ligne déplace cette ligne seule.
                var bands = row.Lines.Where(l => !l.Deleted && l.Cells.Any(c => !string.IsNullOrWhiteSpace(c.Text)))
                    .Select(l => (Line: l, Entry: _view.Lines.FirstOrDefault(x => x.Line == l && !x.CarriedOnly)))
                    .Where(x => x.Entry != null).Select(x => (x.Line, Band: LineBand(x.Entry!)))
                    .Where(x => x.Band is RectD b && b.CenterY > strip.Top && b.CenterY < strip.Bottom)
                    .Select(x => (x.Line, Band: x.Band!.Value)).ToList();
                bool block = bands.Count > 1;
                double h = block ? Math.Max(24, Math.Min(36, bands[0].Band.Height + 6)) : Math.Max(24, Math.Min(40, strip.Bottom - strip.Top - 4));
                double cy = block ? bands[0].Band.CenterY : (strip.Top + strip.Bottom) / 2;
                AddGrip(vt, strip, null, cy, h, block ? "Glisser pour déplacer le bloc" : "Glisser pour déplacer la rangée");
                foreach (var (line, band) in bands.Skip(1))
                    AddGrip(vt, strip with { Top = Math.Max(strip.Top, band.Top - 4), Bottom = Math.Min(strip.Bottom, band.Bottom + 4) }, line,
                        band.CenterY, Math.Max(24, Math.Min(36, band.Height + 6)), "Glisser pour déplacer cette ligne seule (elle devient une rangée)");
            }
        }
    }

    private static readonly SolidColorBrush UnitGripFill = new(ColorHelper.FromArgb(255, 255, 240, 224));
    private static readonly SolidColorBrush UnitGripInk = new(ColorHelper.FromArgb(255, 200, 110, 20));

    private void AddGrip(Arrangement.ViewTable vt, Arrangement.Strip strip, PrintedLine? line, double centerY, double h, string tip, Arrangement.Unit? unit = null)
    {
        var grip = new Border
        {
            // Mode « Modifier » : la poubelle est masquée, la poignée prend sa place, plus large (facile à prendre au doigt).
            Width = line is null ? 72 : 56, Height = h, CornerRadius = new CornerRadius(8), Background = line is null ? GripFill : Paper,
            BorderBrush = GripBorder, BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = "", FontSize = line is null ? 20 : 17, Foreground = GripInk },
        };
        ToolTipService.SetToolTip(grip, tip);
        if (unit != null) { grip.Background = UnitGripFill; ((FontIcon)grip.Child).Foreground = UnitGripInk; }
        Canvas.SetLeft(grip, GutterWidth - 6 - (line is null ? 72 : 56));
        Canvas.SetTop(grip, centerY - h / 2);
        InitDrag(grip, vt, strip, line, unit);
        _gutter.Children.Add(grip);
    }

    /// <summary>Glisser une poignée : rangée entière, ou une ligne seule (<paramref name="line"/>) qui devient une rangée à l'endroit choisi.</summary>
    private void InitDrag(Border grip, Arrangement.ViewTable vt, Arrangement.Strip strip, PrintedLine? line = null, Arrangement.Unit? unit = null)
    {
        int? before = null;
        bool dragging = false, moved = false;
        grip.PointerPressed += (_, e) =>
        {
            dragging = true;
            moved = false;
            grip.CapturePointer(e.Pointer);
            _ghost.Width = _view.Width;
            _ghost.Height = Math.Max(4, strip.Bottom - strip.Top);
            Canvas.SetLeft(_ghost, 0);
            Canvas.SetTop(_ghost, strip.Top);
            _ghost.Visibility = Visibility.Visible;
            _dropLine.Width = _view.Width;
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double y = e.GetCurrentPoint(Root).Position.Y;
            moved = true;
            Canvas.SetTop(_ghost, y - (strip.Bottom - strip.Top) / 2);
            // Point d'insertion : avant la première rangée dont le milieu est sous le doigt.
            int i = vt.Strips.FindIndex(s => y < (s.Top + s.Bottom) / 2);
            before = i < 0 ? null : vt.Strips[i].R;
            double at = i < 0 ? vt.Strips[^1].Bottom : vt.Strips[i].Top;
            Canvas.SetTop(_dropLine, at - 2);
            _dropLine.Visibility = Visibility.Visible;
            e.Handled = true;
        };
        void End(bool ok)
        {
            if (!dragging) return;
            dragging = false;
            _ghost.Visibility = _dropLine.Visibility = Visibility.Collapsed;
            if (!ok || !moved) return;
            if (unit != null) { UnitMoved?.Invoke(this, vt.Table, unit, before); return; }
            if (line != null) { LineMoved?.Invoke(this, vt.Table, line, before); return; }
            if (before == strip.R) return;
            int k = vt.Strips.FindIndex(s => s.R == strip.R);
            if (k + 1 < vt.Strips.Count && before == vt.Strips[k + 1].R) return; // même place
            if (k == vt.Strips.Count - 1 && before is null) return;
            RowMoved?.Invoke(this, vt.Table, strip.R, before);
        }
        grip.PointerReleased += (_, e) => { End(true); grip.ReleasePointerCapture(e.Pointer); e.Handled = true; };
        grip.PointerCanceled += (_, _) => End(false);
        grip.PointerCaptureLost += (_, _) => End(false);
    }

    // ---------- Champs ----------

    private void AddField(Field f, Arrangement.ViewCell cell)
    {
        var line = _place[f].Entry.Line;
        bool multi = TextWrap.IsMultiline(f);
        var tb = new TextBox
        {
            Style = (Style)Application.Current.Resources["FieldBox"],
            Text = ToBox(f.Text),
            TextAlignment = f.Align switch { TextAlign.Right => TextAlignment.Right, TextAlign.Center => TextAlignment.Center, _ => TextAlignment.Left },
            FontWeight = TextPlacement.IsBold(line) ? FontWeights.Bold : FontWeights.Normal,
        };
        if (multi)
        {
            // Désignation : passe à la ligne, même taille d'écriture ; Entrée = nouvelle ligne dans la case.
            tb.TextWrapping = TextWrapping.Wrap;
            tb.AcceptsReturn = true;
            // Correcteur de Windows (fautes soulignées, propositions), sans correction automatique.
            tb.IsSpellCheckEnabled = true;
            tb.Language = "fr-FR";
        }
        if (f.Column > 0 && (f.OriginalText.Length == 0 || f.OriginalText.Any(char.IsDigit)))
        {
            // Clavier tactile numérique pour les colonnes de valeurs (les lettres restent accessibles).
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
            tb.InputScope = scope;
        }
        _boxes[f] = tb;
        Layout(tb, f);
        ApplyVisual(tb, f, focused: false);

        tb.GotFocus += (_, _) => { ApplyVisual(tb, f, focused: true); ShowFrame(f); ShowLoupe(f); FieldFocused?.Invoke(this, f); };
        tb.LostFocus += (_, _) =>
        {
            ApplyVisual(tb, f, focused: false);
            _focusFrame.Visibility = Visibility.Collapsed;
            _loupe.Visibility = Visibility.Collapsed;
        };
        tb.SelectionChanged += (_, _) => _selection = (f, tb.SelectionStart, tb.SelectionStart + tb.SelectionLength);
        tb.TextChanged += (_, _) =>
        {
            string text = FromBox(tb.Text);
            if (f.Text == text) return;
            BeforeEdit?.Invoke(this, f);
            Strikes.Adjust(f, f.Text, text);
            f.Text = text;
            Layout(tb, f);
            ApplyVisual(tb, f, tb.FocusState != FocusState.Unfocused);
            Edited?.Invoke(this, f);
            RefreshStrikes();
        };
        if (!multi)
            tb.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                FocusNextInColumn(f);
            };
        // Clic droit ou appui long : menu de la case (Couper, Copier, Coller) avec, en tête, les actions « Ligne ».
        tb.ContextFlyout = _textMenu;
        tb.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(tb).Properties.IsRightButtonPressed) _rightPress = (f, Environment.TickCount64);
        }), handledEventsToo: true);
        _fieldLayer.Children.Add(tb);
    }

    /// <summary>Ouverture du menu d'une case : les actions « Ligne » sont mises à jour pour cette case.</summary>
    private void OnTextMenuOpening(object? sender, object e)
    {
        if (sender is not CommandBarFlyout menu) return;
        foreach (var old in menu.SecondaryCommands.Where(c => (c as FrameworkElement)?.Tag as string == LineTag).ToList())
            menu.SecondaryCommands.Remove(old);
        Field? f = _boxes.FirstOrDefault(kv => ReferenceEquals(kv.Value, menu.Target)).Key;
        if (_rightPress is { } press && Environment.TickCount64 - press.At < 1500) f = press.Field;
        _rightPress = null;
        if (f is null || LineCommands is null) return;
        int at = 0;
        foreach (var c in LineCommands(f))
        {
            var button = new AppBarButton { Label = c.Label, Icon = new FontIcon { Glyph = c.Glyph }, Tag = LineTag };
            if (c.Danger) button.Foreground = TrashInk;
            ToolTipService.SetToolTip(button, c.Hint);
            button.Click += (_, _) => { menu.Hide(); c.Run(); };
            menu.SecondaryCommands.Insert(at++, button);
        }
        if (at > 0) menu.SecondaryCommands.Insert(at, new AppBarSeparator { Tag = LineTag });
    }

    /// <summary>Repère bref sur une ligne (rangée ajoutée ou séparée).</summary>
    public void FlashLine(PrintedLine line)
    {
        var entry = _view.Lines.FirstOrDefault(e => e.Line == line);
        if (entry is null || entry.Cells.Count == 0) return;
        var box = entry.Cells.Aggregate(RectD.Empty, (r, c) => r.Union(c.Box));
        var mark = new Rectangle
        {
            Width = box.Width + 6, Height = box.Height + 6, RadiusX = 4, RadiusY = 4, Stroke = Accent, StrokeThickness = 3,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(40, 10, 102, 194)),
        };
        Canvas.SetLeft(mark, box.X - 3);
        Canvas.SetTop(mark, box.Y - 3);
        _overlayLayer.Children.Add(mark);
        var timer = _overlayLayer.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1800);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _overlayLayer.Children.Remove(mark);
        timer.Start();
    }

    /// <summary>Retours à la ligne : « \n » dans le projet (comme sur iPhone/iPad), « \r » dans les champs Windows.</summary>
    private static string ToBox(string s) => s.Replace("\r\n", "\n").Replace('\n', '\r');
    private static string FromBox(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Taille de police de référence : celle qui redonne la largeur du texte imprimé
    /// (plus fiable que la hauteur des boîtes OCR), bornée autour de l'estimation du cœur.
    /// </summary>
    private static double BaseFontSize(Field f, double boxHeight, bool bold)
    {
        double size = f.FontSize;
        if (!f.Ink.IsEmpty && f.OriginalText.Length > 0)
        {
            double w100 = Measure(f.OriginalText, 100, bold);
            if (w100 > 0) size = Math.Clamp(100 * f.Ink.Width / w100, 0.7 * f.FontSize, 1.3 * f.FontSize);
        }
        return Math.Max(5, Math.Min(size, boxHeight / LineHeightRatio));
    }

    /// <summary>
    /// Mise en page du texte d'une case (en pixels de la page d'origine) : désignation à la taille de l'imprimé sur
    /// plusieurs lignes, valeurs sur une ligne réduites si besoin ; 1re ligne à la hauteur du texte d'origine.
    /// </summary>
    public static (double Size, List<string> Lines, double Top) TextLayout(PrintedLine line, Field f, string text)
    {
        bool bold = TextPlacement.IsBold(line);
        return TextPlacement.Layout(line, f, text, BaseFontSize(f, PageLayout.BaseBox(line, f).Height, bold), (s, z) => Measure(s, z, bold));
    }

    /// <summary>Hauteur à ajouter sous la ligne pour que le texte de toutes ses cases tienne.</summary>
    public static int LineExtra(PrintedLine line)
    {
        int extra = 0;
        foreach (var f in line.Cells.Where(c => TextWrap.IsMultiline(c) && !c.Locked))
        {
            var t = TextLayout(line, f, f.Text);
            extra = Math.Max(extra, PageLayout.ExtraFor(line, f, t.Lines.Count, t.Size));
        }
        return extra;
    }

    private void Layout(TextBox tb, Field f)
    {
        var (cell, entry) = _place[f];
        var (size, _, top) = TextLayout(entry.Line, f, FromBox(tb.Text));
        var box = cell.Box;
        tb.FontSize = size;
        Canvas.SetLeft(tb, box.X);
        Canvas.SetTop(tb, box.Y);
        tb.Width = Math.Max(1, box.Width);
        tb.Height = Math.Max(1, box.Height);
        double pad = Math.Max(0, top + cell.Dy - box.Y);
        double left = Math.Max(0, f.Box.X + cell.Dx + f.Indent - box.X);
        double right = Math.Max(0, box.Right - (f.Box.Right + cell.Dx - f.Indent));
        tb.Padding = f.Align switch
        {
            TextAlign.Right => new Thickness(0, pad, right, 0),
            TextAlign.Center => new Thickness(0, pad, 0, 0),
            _ => new Thickness(left, pad, TextWrap.IsMultiline(f) ? 3 : 0, 0),
        };
    }

    private static double Measure(string s, double size, bool bold)
    {
        Meter.FontSize = size;
        Meter.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        Meter.Text = s;
        Meter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Meter.DesiredSize.Width;
    }

    /// <summary>
    /// Case non modifiée : totalement invisible (opacité 0, mais toujours touchable), on ne voit que l'original.
    /// Case modifiée ou en cours d'édition : fond du papier (ou trame grise de la rangée) qui masque l'original.
    /// </summary>
    private void ApplyVisual(TextBox tb, Field f, bool focused)
    {
        bool onImage = _image != null;
        bool show = focused || f.IsModified || !onImage;
        tb.Opacity = show ? 1 : 0;
        tb.Background = onImage ? PaperOf(f) : Clear;
        DrawColors(f, tb, focused);
    }

    private Brush PaperOf(Field f)
    {
        if (_image is null) return Paper;
        if (_paper.TryGetValue(f, out var brush)) return brush;
        var (cell, entry) = _place[f];
        if (cell.Dx == 0 && ImageExporter.CleanPatch(_image, entry.Page, entry.Line, f, cell.Dy) is RectD patch)
        {
            // Fond tramé (total, en-tête) : le motif de la rangée répété, un aplat blanc se verrait.
            var tile = ImageExporter.TilePatch(_image, patch, (int)Math.Ceiling(cell.Box.Width), (int)Math.Ceiling(cell.Box.Height));
            brush = new ImageBrush { ImageSource = WinImaging.ToBitmap(tile), Stretch = Stretch.Fill };
        }
        else
        {
            var (r, g, b) = ImageExporter.PaperColor(_image, cell.Box);
            brush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        }
        _paper[f] = brush;
        return brush;
    }

    // ---------- Couleur et traits ----------

    /// <summary>Texte retapé avec des couleurs : écrit par-dessus la case (une zone de saisie ne sait écrire qu'en une couleur).</summary>
    public void RefreshColors()
    {
        foreach (var (f, tb) in _boxes) DrawColors(f, tb, tb.FocusState != FocusState.Unfocused);
    }

    private void DrawColors(Field f, TextBox tb, bool focused)
    {
        bool show = !focused && f.IsModified && f.Colors.Count > 0 && f.Text.Length > 0;
        tb.Foreground = show ? Clear : Ink;
        if (!show)
        {
            if (_colorTexts.Remove(f, out var old)) _colorLayer.Children.Remove(old);
            return;
        }
        if (!_colorTexts.TryGetValue(f, out var block))
        {
            block = new TextBlock { FontFamily = new FontFamily("Times New Roman"), IsHitTestVisible = false };
            _colorTexts[f] = block;
            _colorLayer.Children.Add(block);
        }
        block.Inlines.Clear();
        foreach (var (start, end, color) in Strikes.ColorRuns(f))
        {
            var (r, g, b) = Strikes.TextRgb(color);
            block.Inlines.Add(new Run { Text = f.Text[start..end].Replace('\n', '\r'), Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b)) });
        }
        block.FontSize = tb.FontSize;
        block.FontWeight = tb.FontWeight;
        block.TextAlignment = tb.TextAlignment;
        block.TextWrapping = TextWrap.IsMultiline(f) ? TextWrapping.Wrap : TextWrapping.NoWrap;
        block.LineHeight = LineHeightRatio * tb.FontSize;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.Padding = tb.Padding;
        block.Width = tb.Width;
        block.Height = tb.Height;
        Canvas.SetLeft(block, Canvas.GetLeft(tb));
        Canvas.SetTop(block, Canvas.GetTop(tb));
    }

    /// <summary>Traits des morceaux barrés, posés par-dessus le texte (scan ou texte retapé).</summary>
    public void RefreshStrikes()
    {
        _strikeLayer.Children.Clear();
        foreach (var (f, (cell, entry)) in _place)
        {
            bool bold = TextPlacement.IsBold(entry.Line);
            double size = BaseFontSize(f, PageLayout.BaseBox(entry.Line, f).Height, bold);
            foreach (var g in TextPlacement.StrikeSegments(entry.Line, f, size, (s, z) => Measure(s, z, bold)))
            {
                var (r, gr, b) = TextPlacement.Rgb(g.Color);
                var bar = new Rectangle { Width = g.X1 - g.X0, Height = g.Thickness, Fill = new SolidColorBrush(ColorHelper.FromArgb(255, r, gr, b)) };
                Canvas.SetLeft(bar, g.X0 + cell.Dx);
                Canvas.SetTop(bar, g.Y + cell.Dy - g.Thickness / 2);
                _strikeLayer.Children.Add(bar);
            }
        }
    }

    // ---------- Loupe, cadre, case suivante ----------

    /// <summary>Pendant l'édition, l'original de la case est montré en loupe juste au-dessus.</summary>
    /// <summary>Loupe « Original » au-dessus de la case touchée (réglage Mise en page).</summary>
    public static bool LoupeEnabled { get; set; } = true;

    private void ShowLoupe(Field f)
    {
        if (_bitmap is null || !LoupeEnabled) return;
        var box = _place[f].Cell.Box;
        var b = RectD.FromEdges(Math.Max(0, box.Left - 4), Math.Max(0, box.Top - 2), Math.Min(_view.Width, box.Right + 4), Math.Min(_view.Height, box.Bottom + 2));
        var holder = new Canvas { Width = b.Width, Height = b.Height, Clip = new RectangleGeometry { Rect = new Rect(0, 0, b.Width, b.Height) } };
        var img = new Image { Source = _bitmap, Width = _view.Width, Height = _view.Height, Stretch = Stretch.Fill };
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
        Canvas.SetLeft(_loupe, Math.Max(0, Math.Min(b.Left, _view.Width - (b.Width + 4) * scale)));
        Canvas.SetTop(_loupe, top);
        _loupe.Visibility = Visibility.Visible;
    }

    private void ShowFrame(Field f)
    {
        var box = _place[f].Cell.Box;
        _focusFrame.Width = box.Width + 4;
        _focusFrame.Height = box.Height + 4;
        Canvas.SetLeft(_focusFrame, box.X - 2);
        Canvas.SetTop(_focusFrame, box.Y - 2);
        _focusFrame.Visibility = Visibility.Visible;
    }

    /// <summary>Entrée : passe à la case suivante de la même colonne (saisie d'une colonne de valeurs).</summary>
    private void FocusNextInColumn(Field f)
    {
        var column = _place.Where(p => p.Key.Column == f.Column && _boxes.ContainsKey(p.Key)).OrderBy(p => p.Value.Cell.Box.Y).Select(p => p.Key).ToList();
        int i = column.IndexOf(f);
        if (i >= 0 && i + 1 < column.Count) _boxes[column[i + 1]].Focus(FocusState.Programmatic);
    }

    /// <summary>Pages TXT/CSV/XLSX : le quadrillage du tableau, tel qu'affiché.</summary>
    private Canvas DrawGrid()
    {
        var c = new Canvas();
        foreach (var t in _view.Tables)
        {
            if (t.RowYs.Count == 0 || t.ColumnXs.Count == 0) continue;
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
        // Écran → page d'origine (des rangées ont pu bouger).
        int? y0 = _unmapY((int)Math.Round(Math.Min(a.Y, b.Y))), y1 = _unmapY((int)Math.Round(Math.Max(a.Y, b.Y)));
        if (y0 is null || y1 is null) return;
        var zone = RectD.FromEdges(Math.Min(a.X, b.X), y0.Value, Math.Max(a.X, b.X), y1.Value);
        bool changed;
        BeforeChange?.Invoke(this);
        if (zone.Width < 8 && zone.Height < 8)
            changed = PageEdits.RemoveLockZoneAt(Page, b.X, _unmapY((int)Math.Round(b.Y)) ?? -1);
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
