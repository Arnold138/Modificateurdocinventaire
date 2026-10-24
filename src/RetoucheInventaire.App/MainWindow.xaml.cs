using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using RetoucheInventaire.App.Services;
using RetoucheInventaire.App.Views;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Export;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Import;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Storage;
using Windows.Storage;
using Windows.Storage.Pickers;
using ModelPage = RetoucheInventaire.Core.Model.Page;

namespace RetoucheInventaire.App;

/// <summary>
/// L'écran unique, identique à la version iPhone/iPad : importer → corriger sur la page → exporter.
/// Une seule page est affichée à la fois (« 3 / 10 », ◀ ▶) : les longs inventaires restent fluides.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string DataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetoucheInventaire");
    /// <summary>Copie de secours du travail en cours, rechargée au lancement.</summary>
    private static readonly string AutosavePath = System.IO.Path.Combine(DataDir, "en-cours" + ProjectStore.Extension);
    /// <summary>Chemin du fichier projet choisi par l'utilisateur, pour continuer à y enregistrer après un redémarrage.</summary>
    private static readonly string LastPathFile = System.IO.Path.Combine(DataDir, "dernier-projet.txt");

    private readonly OcrService _ocr = new();
    private readonly ImportService _import;
    private readonly DispatcherQueueTimer _autosaveTimer;

    private InventoryProject _project = new();
    private readonly Dictionary<string, byte[]> _sources = [];   // entrée d'archive → fichier source
    // Images gardées en mémoire pour quelques pages récentes seulement.
    private readonly Dictionary<ModelPage, Raster> _flats = [];     // page → image redressée
    private readonly Dictionary<ModelPage, Raster> _oriented = [];  // page → photo d'origine orientée
    private readonly List<ModelPage> _recent = [];
    private int _index;
    private PageView? _view;
    private Field? _currentField;
    private string? _projectPath;
    private bool _busy;

    private ModelPage? CurrentPage => _index >= 0 && _index < _project.Pages.Count ? _project.Pages[_index] : null;

    public MainWindow()
    {
        InitializeComponent();
        _import = new ImportService(_ocr);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        _autosaveTimer = DispatcherQueue.CreateTimer();
        _autosaveTimer.Interval = TimeSpan.FromSeconds(1);
        _autosaveTimer.IsRepeating = false;
        _autosaveTimer.Tick += (_, _) => Autosave();

        InitCropHandles();
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            EmptyIcon.Source = new BitmapImage(new Uri(icon));
        }
        Root.KeyDown += Root_KeyDown;
        Root.Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Autosave();
    }

    private async Task StartAsync()
    {
        if (!_ocr.IsAvailable)
            SetStatus("Reconnaissance du texte indisponible : installez la reconnaissance optique française de Windows (voir le README). TXT, CSV et XLSX restent utilisables.");
        else if (!_ocr.IsFrench)
            SetStatus($"Attention : la reconnaissance du texte est en « {_ocr.LanguageTag} », pas en français (voir le README).");
        else
            SetStatus("Prêt. Fonctionne sans Internet : aucun document ne quitte la tablette.");

        if (File.Exists(AutosavePath))
        {
            await RunBusyAsync(async () =>
            {
                try
                {
                    await OpenProjectAsync(AutosavePath, rememberPath: false);
                    if (File.Exists(LastPathFile)) _projectPath = File.ReadAllText(LastPathFile).Trim() is { Length: > 0 } p ? p : null;
                    SetStatus($"Travail en cours rechargé ({_project.Pages.Count} page(s))."
                              + (_projectPath is null ? "" : $" Enregistrement continu dans {System.IO.Path.GetFileName(_projectPath)}."));
                    ShowSaveState();
                }
                catch
                {
                    SetStatus("Le travail en cours n'a pas pu être rechargé.");
                }
            });
        }
        UpdateCommands();
    }

    // ---------- Mémoire : quelques pages récentes seulement ----------

    private void Remember(Dictionary<ModelPage, Raster> map, ModelPage page, Raster value, int max)
    {
        map[page] = value;
        _recent.Remove(page);
        _recent.Add(page);
        foreach (var old in _recent.Where(p => map.ContainsKey(p)).SkipLast(max).ToList()) map.Remove(old);
    }

    private void Forget(ModelPage page)
    {
        _flats.Remove(page);
        _oriented.Remove(page);
        _recent.Remove(page);
    }

    // ---------- Import ----------

    private async void Photo_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file;
        try
        {
            var camera = new Microsoft.Windows.Media.Capture.CameraCaptureUI(AppWindow.Id);
            camera.PhotoSettings.Format = Microsoft.Windows.Media.Capture.CameraCaptureUIPhotoFormat.Jpeg;
            file = await camera.CaptureFileAsync(Microsoft.Windows.Media.Capture.CameraCaptureUIMode.Photo);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Caméra indisponible", $"La caméra n'a pas pu être ouverte ({ex.Message}). Utilisez « Importer » pour ouvrir une photo.");
            return;
        }
        if (file is null) return;
        await RunBusyAsync(() => ImportFilesAsync([file], $"Photo {DateTime.Now:dd-MM-yyyy HH-mm}.jpg"));
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitPicker(picker);
        foreach (var ext in ImportService.Extensions) picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add(ProjectStore.Extension);
        picker.FileTypeFilter.Add(".zip");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await RunBusyAsync(() => ImportFilesAsync(files, null));
    }

    /// <summary>Un dossier entier : toutes les photos, PDF et tableaux qu'il contient (sous-dossiers compris).</summary>
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunBusyAsync(async () =>
        {
            var files = new List<StorageFile>();
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
                if (Importable(path)) files.Add(await StorageFile.GetFileFromPathAsync(path));
            await ImportFilesAsync(files, null, folder.Path, folder.Name);
        });
    }

    private static bool Importable(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return !System.IO.Path.GetFileName(path).StartsWith('.')
               && (ImportService.Extensions.Contains(ext) || ext == ProjectStore.Extension || ext == ".zip");
    }

    /// <summary>
    /// Fichiers à importer, dans l'ordre des noms (« page 2 » avant « page 10 ») ; un .zip est ouvert
    /// et son contenu utilisable ajouté (le reste est ignoré).
    /// </summary>
    private static async Task<List<(StorageFile File, string Name, string Key)>> ExpandAsync(IReadOnlyList<StorageFile> files, string? root)
    {
        var items = new List<(StorageFile File, string Name, string Key)>();
        foreach (var file in files)
        {
            string key = root != null && file.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Path[root.Length..] : file.Name;
            if (!System.IO.Path.GetExtension(file.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                items.Add((file, Names.Repair(file.Name), Names.Repair(key)));
                continue;
            }
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RetoucheInventaire", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(file.Path, dir, System.Text.Encoding.UTF8, overwriteFiles: true));
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = path[(dir.Length + 1)..];
                if (rel.StartsWith("__MACOSX") || !Importable(path) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add((await StorageFile.GetFileFromPathAsync(path), Names.Repair(System.IO.Path.GetFileName(path)), Names.Repair(key + "/" + rel)));
            }
        }
        items.Sort((a, b) => Names.NaturalCompare(a.Key, b.Key));
        return items;
    }

    private async Task ImportFilesAsync(IReadOnlyList<StorageFile> files, string? forcedName, string? root = null, string? folderName = null)
    {
        int firstNew = _project.Pages.Count, added = 0;
        bool wasEmpty = firstNew == 0;
        var sorted = await ExpandAsync(files, root);
        if (sorted.Count == 0) { await ShowMessageAsync("Rien à importer", "Aucune photo, PDF ou tableau trouvé dans ce que vous avez choisi."); return; }
        for (int k = 0; k < sorted.Count; k++)
        {
            var (file, fileName, _) = sorted[k];
            string name = forcedName ?? fileName;
            int fileIndex = k;
            // Progression globale : fichier k sur N, et page i sur n à l'intérieur d'un PDF.
            void Step(double f, string text)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    Progress.Value = (fileIndex + f) / sorted.Count;
                    SetStatus(sorted.Count > 1 && !text.StartsWith("Page ") ? $"Fichier {fileIndex + 1} sur {sorted.Count} · {text}" : text);
                });
            }
            try
            {
                if (System.IO.Path.GetExtension(file.Name).Equals(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    await OpenProjectAsync(file.Path, rememberPath: true);
                    RememberProjectPath();
                    firstNew = _project.Pages.Count;
                    continue;
                }
                await foreach (var imported in _import.ImportAsync(file, name, _sources, Step))
                {
                    await AddPageAsync(imported);
                    added++;
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Import impossible", $"{name} : {ex.Message}");
            }
        }
        if (added == 0) return;
        if (wasEmpty) _project.Name = folderName != null ? Names.Repair(folderName) : System.IO.Path.GetFileNameWithoutExtension(forcedName ?? sorted[0].Name);
        // On revient sur la première page importée.
        if (_index != firstNew && firstNew < _project.Pages.Count) await ShowPageAsync(firstNew);
        Autosave();
        UpdateCommands();
        SetStatus($"{added} page(s) ajoutée(s). Touchez une case pour la corriger ; ◀ ▶ en haut pour changer de page.");
    }

    /// <summary>Ajoute une page, l'affiche et enregistre aussitôt : rien n'est perdu si l'application est fermée en route.</summary>
    private async Task AddPageAsync(ImportedPage imported)
    {
        _project.Pages.Add(imported.Page);
        Lots.Resync(_project);
        if (imported.OrientedSource != null) Remember(_oriented, imported.Page, imported.OrientedSource, 2);
        await ShowPageAsync(_project.Pages.Count - 1, imported.Image);
        Autosave();
    }

    // ---------- Mise en page et images ----------

    /// <summary>Mise en page : lots 0 recollés après le dernier lot, rangées déplacées à la main.</summary>
    private Arrangement.Plan _plan = new([], [], null, []);
    /// <summary>Morceaux d'image des lots 0 (pour recomposer la page où ils arrivent sans rouvrir leur page).</summary>
    private readonly Dictionary<string, Arrangement.Crop?> _crops = [];

    private Arrangement.Plan Replan() => _plan = Arrangement.Build(_project);

    /// <summary>Totaux recalculés (cases non tapées à la main) ; renvoie les cases changées.</summary>
    private List<Field> RecomputeTotals() => Totals.Recompute(_project, _plan);

    private static string ColorSignature(ModelPage page) =>
        string.Join(";", page.AllFields().Where(f => f.Colors.Count > 0).Select(f => f.Id + (f.IsModified ? "m" : "") + string.Join(",", f.Colors.Select(c => $"{c.Start}-{c.Length}-{c.Color}"))));

    /// <summary>Image de la page avant corrections : redressée, agrandie, colonne des lots tracée, écriture en couleur.</summary>
    private async Task<Raster?> OwnDisplayAsync(ModelPage page)
    {
        if (page.Source != PageSource.Image) return null;
        var flat = _flats.TryGetValue(page, out var cached) ? cached : await RestoreFlatAsync(page);
        if (flat is null) return null;
        Remember(_flats, page, flat, 3);
        return ImageExportService.PageRaster(page, PageLayout.Stretch(flat, page), _project.LotFrame);
    }

    private async Task<Arrangement.Crop?> CropOfAsync(Arrangement.Unit u)
    {
        if (u.Page.Source != PageSource.Image) return null;
        string key = string.Join("|", u.Page.Id, ImageKey(u.Page), LotColumn.Signature(u.Page, _project.LotFrame), ColorSignature(u.Page), u.BodyTop, u.Bottom, u.Title != null);
        if (_crops.TryGetValue(key, out var crop)) return crop;
        var own = await OwnDisplayAsync(u.Page);
        crop = own is null ? null : Arrangement.UnitCrop(u, own);
        if (_crops.Count > 60) _crops.Clear();
        _crops[key] = crop;
        return crop;
    }

    /// <summary>Image affichée de la page k : la sienne, recomposée si des rangées partent ou arrivent.</summary>
    private async Task<Raster?> DisplayOfAsync(int k)
    {
        var own = await OwnDisplayAsync(_project.Pages[k]);
        if (own is null || !Arrangement.PageArranged(_project, _plan, k)) return own;
        var crops = new Dictionary<Arrangement.Unit, Arrangement.Crop?>();
        foreach (var pc in _plan.Pages[k].Pieces.Where(pc => pc.IsUnit)) crops[pc.Unit!] = await CropOfAsync(pc.Unit!);
        return Arrangement.ComposePage(_plan, k, own, crops);
    }

    // ---------- Affichage d'une page ----------

    /// <summary>Affiche la page n (et seulement elle).</summary>
    private async Task ShowPageAsync(int n, Raster? flat = null, bool fit = true, bool keepScroll = false)
    {
        double scrollX = PageScroller.HorizontalOffset, scrollY = PageScroller.VerticalOffset;
        float scrollZoom = PageScroller.ZoomFactor;
        var focused = keepScroll ? _currentField : null;
        PagesPanel.Children.Clear();
        _view = null;
        _currentField = null;
        if (_project.Pages.Count == 0) { _index = 0; UpdateCommands(); return; }
        _index = Math.Clamp(n, 0, _project.Pages.Count - 1);
        var page = _project.Pages[_index];
        UpdateCommands();
        if (flat != null) Remember(_flats, page, flat, 3);
        Replan();
        if (page.Source == PageSource.Image && !_flats.ContainsKey(page)) SetStatus($"Ouverture de la page {_index + 1}…");
        int k = _index;
        var display = await DisplayOfAsync(k);
        if (CurrentPage != page) return; // l'utilisateur a déjà changé de page

        var title = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7,
                                    Text = $"Page {_index + 1} sur {_project.Pages.Count} · {page.SourceName}" };
        var remove = new Button { Content = "Retirer la page", MinHeight = 40, Padding = new Thickness(14, 4, 14, 4), CornerRadius = new CornerRadius(20) };
        remove.Click += async (_, _) => await RemovePageAsync(page);
        var sync = new Button
        {
            MinHeight = 40, Padding = new Thickness(14, 4, 14, 4), CornerRadius = new CornerRadius(20),
            Style = (Style)Application.Current.Resources["PillButton"],
            Visibility = Lots.IsActive(_project) ? Visibility.Visible : Visibility.Collapsed,
        };
        var syncContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        syncContent.Children.Add(new FontIcon { Glyph = "", FontSize = 15 });
        syncContent.Children.Add(new TextBlock { Text = "Synchroniser automatiquement" });
        sync.Content = syncContent;
        sync.Click += (_, _) => { PushUndo(); SyncLots(); };
        _syncButton = sync;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(PageView.GutterWidth, 0, 0, 0) };
        header.Children.Add(title);
        header.Children.Add(sync);
        header.Children.Add(remove);

        var view = new PageView(Arrangement.ViewOf(_project, _plan, k), display, () => Lots.IsActive(_project),
            y => Arrangement.MapY(_plan.Pages[k], y), y => Arrangement.UnmapY(_plan.Pages[k], y));
        view.FieldFocused += (_, f) => { _currentField = f; _editSession = null; UpdateEditBar(); };
        view.BeforeEdit += (_, f) => { if (_editSession != f) { PushUndo(); _editSession = f; } };
        view.BeforeChange += _ => PushUndo();
        view.Edited += (v, f) =>
        {
            if (v.LineOf(f) is { } line && Totals.IsComputedCell(line, f)) f.Manual = true; // total tapé : il ne bouge plus tout seul
            AfterEdit(v, f);
            UpdateEditBar();
            ScheduleAutosave();
        };
        view.LocksChanged += _ => { ScheduleAutosave(); SetStatus("Zones verrouillées mises à jour."); };
        view.LotChanged += async (v, line, value) =>
        {
            PushUndo();
            bool wasZero = Lots.IsZero(line);
            string sig = LotColumn.Signature(v.Page, _project.LotFrame);
            Lots.Set(_project, line, value);
            bool zero = Lots.IsZero(line);
            Replan();
            RecomputeTotals();
            if (zero != wasZero || sig != LotColumn.Signature(v.Page, _project.LotFrame)) await RefreshViewAsync();
            else v.UpdateLots();
            if (zero && !wasZero)
                SetStatus(_plan.Dest is { } d
                    ? $"Lot 0 : « {line.FirstText} » est passé après le dernier lot (page {d.P + 1}). Retapez un numéro pour le remettre à sa place."
                    : "Lot 0 : aucun autre lot numéroté, la ligne reste en place.");
            else if (wasZero && !zero) SetStatus($"Lot {line.Lot} : « {line.FirstText} » est revenu à sa place.");
            else SetStatus(line.Lot.Length > 0 ? $"Lot {line.Lot} enregistré." : "Case de lot vidée.");
            ScheduleAutosave();
        };
        view.LotRemoved += async (_, line) =>
        {
            PushUndo();
            Lots.Remove(_project, line);
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            SetStatus("Case de lot retirée : cette ligne n'est pas un objet.");
            ScheduleAutosave();
        };
        view.LotAdded += async (_, line) =>
        {
            PushUndo();
            Lots.Add(_project, line);
            await RefreshViewAsync();
            SetStatus("Case de lot ajoutée : tapez le numéro.");
            ScheduleAutosave();
        };
        view.RowMoved += async (_, table, movedRow, beforeRow) =>
        {
            PushUndo();
            var order = Lots.RowOrder(table).Where(r => r != movedRow).ToList();
            order.Insert(beforeRow is int b ? order.IndexOf(b) : order.Count, movedRow);
            table.RowOrder = order;
            await RefreshViewAsync();
            SetStatus("Rangée déplacée. ↶ pour annuler.");
            ScheduleAutosave();
        };
        view.SetLockMode(LockToggle.IsChecked == true);
        _view = view;
        PagesPanel.Children.Add(header);
        PagesPanel.Children.Add(view.Host);
        if (!keepScroll)
            SetStatus(page.Source == PageSource.Image
                ? "Touchez une case pour la corriger ; l'original s'affiche en loupe au-dessus."
                : "Touchez une case pour la corriger.");
        if (keepScroll) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => PageScroller.ChangeView(scrollX, scrollY, scrollZoom, disableAnimation: true));
        else if (fit) FitWidthSoon();
        else PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (focused != null && view.Shows(focused)) { _currentField = focused; view.Focus(focused); }
        UpdateCommands();
    }

    /// <summary>Reconstruit la page affichée (même zoom, même endroit).</summary>
    private Task RefreshViewAsync() => ShowPageAsync(_index, keepScroll: true);

    /// <summary>La page a changé (rangée agrandie, couleur…) : nouvelle image et cases replacées, sans quitter la case.</summary>
    private async Task RedrawAsync(PageView view)
    {
        Replan();
        int k = _index;
        var display = await DisplayOfAsync(k);
        if (_view != view) return;
        view.Relayout(Arrangement.ViewOf(_project, _plan, k), display);
    }

    /// <summary>
    /// Après une saisie : la rangée grandit si le texte passe sur plus de lignes (tout ce qui est en dessous descend),
    /// les totaux suivent, et si une ligne devient (ou cesse d'être) un objet, sa case de lot apparaît ou disparaît.
    /// </summary>
    private void AfterEdit(PageView view, Field? field = null)
    {
        var entry = field is null ? null : view.EntryOf(field);
        bool grew = entry != null && PageLayout.SetExtra(entry.Page, entry.Line, PageView.LineExtra(entry.Line));
        var changed = RecomputeTotals();
        bool lots = false;
        if (Lots.IsActive(_project))
        {
            int before = Lots.ObjectLines(_project).Count();
            Lots.Resync(_project);
            lots = Lots.ObjectLines(_project).Count() != before;
        }
        if (grew || lots) _ = RedrawAsync(view);
        else if (changed.Count > 0) view.SyncFields(changed);
    }

    private Button? _syncButton;

    /// <summary>« Synchroniser automatiquement » : le logiciel remplit toutes les cases (les numéros tapés restent des repères).</summary>
    private void SyncLots()
    {
        Lots.FillAutomatically(_project);
        Replan();
        RecomputeTotals();
        _ = RefreshViewAsync();
        var objects = Lots.ObjectLines(_project).Where(o => !Lots.IsZero(o.Line)).ToList();
        SetStatus(objects.Count > 0
            ? $"Lots synchronisés : {objects.Count} objets, de {objects[0].Line.Lot} à {objects[^1].Line.Lot}."
            : "Aucun objet à numéroter.");
        ScheduleAutosave();
    }

    private void SyncLots_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Lots.IsActive(_project)) return;
        PushUndo();
        SyncLots();
    }

    private async void RemovePage_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPage is { } page) await RemovePageAsync(page);
    }

    private async void GoTo(int n)
    {
        if (_busy || n == _index || n < 0 || n >= _project.Pages.Count) return;
        Autosave();
        await RunBusyAsync(() => ShowPageAsync(n));
    }

    private void PagePrev_Click(object sender, RoutedEventArgs e) => GoTo(_index - 1);
    private void PageNext_Click(object sender, RoutedEventArgs e) => GoTo(_index + 1);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Windows.System.VirtualKey.PageUp || e.Key == Windows.System.VirtualKey.Left) { GoTo(_index - 1); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.PageDown || e.Key == Windows.System.VirtualKey.Right) { GoTo(_index + 1); e.Handled = true; }
    }

    /// <summary>Liste des pages : toucher une page pour y aller.</summary>
    private async void PageLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _project.Pages.Count == 0) return;
        int? target = null;
        ContentDialog? dialog = null;
        var list = new StackPanel { Spacing = 4 };
        for (int i = 0; i < _project.Pages.Count; i++)
        {
            var p = _project.Pages[i];
            int edits = p.AllFields().Count(f => f.IsModified);
            int index = i;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var num = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(i == _index ? ColorHelper.FromArgb(255, 10, 102, 194) : ColorHelper.FromArgb(255, 232, 241, 251)),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(i == _index ? Colors.White : ColorHelper.FromArgb(255, 10, 102, 194)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = p.SourceName, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = edits > 0 ? $"{edits} case(s) modifiée(s)" : "non modifiée", FontSize = 13, Opacity = 0.65 });
            Grid.SetColumn(text, 1);
            row.Children.Add(num);
            row.Children.Add(text);
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                                      Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            button.Click += (_, _) => { target = index; dialog?.Hide(); };
            list.Children.Add(button);
        }
        dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = $"{_project.Pages.Count} page(s)",
            Content = new ScrollViewer { Content = list, MaxHeight = 520 },
            CloseButtonText = "Fermer",
        };
        await dialog.ShowAsync();
        if (target is int t) GoTo(t);
    }

    private async Task RemovePageAsync(ModelPage page)
    {
        if (_busy || !await ConfirmAsync("Retirer cette page ?", $"« {page.SourceName} » sera retirée du projet et des exports.", "Retirer")) return;
        int i = _project.Pages.IndexOf(page);
        if (i < 0) return;
        PushUndo();
        _project.Pages.RemoveAt(i);
        Forget(page);
        if (page.SourceEntry != null && _project.Pages.All(p => p.SourceEntry != page.SourceEntry)) _sources.Remove(page.SourceEntry);
        Lots.Resync(_project);
        _crops.Clear();
        RecomputeTotals();
        await ShowPageAsync(Math.Min(i, _project.Pages.Count - 1));
        ScheduleAutosave();
    }

    // ---------- Édition ----------

    private void UpdateEditBar()
    {
        var found = _currentField is null ? null : PageEdits.FindLine(_project, _currentField);
        EditBar.Visibility = found is null ? Visibility.Collapsed : Visibility.Visible;
        if (found is not { } hit) return;
        var f = _currentField!;
        KindButtonText.Text = PageEdits.Label(hit.Line.Kind);
        RevertButton.IsEnabled = !_busy && f.IsModified;
        KindButton.IsEnabled = StrikeButton.IsEnabled = ColorButton.IsEnabled = !_busy;
        _ = StrikeColor;
        bool computed = Totals.IsComputedCell(hit.Line, f);
        RecalcButton.Visibility = computed && f.Manual ? Visibility.Visible : Visibility.Collapsed;
        string text = hit.Line.FirstText.Length > 48 ? hit.Line.FirstText[..48] + "…" : hit.Line.FirstText;
        if (computed)
            EditInfo.Text = f.Manual
                ? $"Total tapé à la main · calculé : {ComputedText(f)}"
                : $"Total calculé automatiquement{(f.IsModified ? $" (imprimé : {(f.OriginalText.Length > 0 ? f.OriginalText : "illisible")})" : "")}";
        else
            EditInfo.Text = f.IsModified
                ? $"Original : « {(f.OriginalText.Length > 0 ? f.OriginalText : "(vide)")} »"
                : $"« {text} »";
    }

    private string ComputedText(Field f)
    {
        var c = Totals.ComputedValue(_project, _plan, f);
        return c.Length > 0 ? c : "(vide)";
    }

    private async void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is null || _view is null) return;
        if (PageEdits.FindLine(_project, _currentField) is not { } hit) return;
        PushUndo();
        PageEdits.CycleKind(hit.Line);
        Lots.Resync(_project);
        Replan();
        RecomputeTotals();
        await RefreshViewAsync();
        UpdateEditBar();
        SetStatus($"Type de ligne : {PageEdits.Label(hit.Line.Kind)}.");
        ScheduleAutosave();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is null || _view is null) return;
        PushUndo();
        if (_view.LineOf(_currentField) is { } line && Totals.IsComputedCell(line, _currentField)) _currentField.Manual = true; // total : on garde le montant imprimé
        _view.Revert(_currentField);
        AfterEdit(_view, _currentField);
        UpdateEditBar();
        ScheduleAutosave();
    }

    private static readonly string StrikeColorFile = System.IO.Path.Combine(DataDir, "couleur-trait.txt");
    private string? _strikeColor;

    private string StrikeColor
    {
        get
        {
            if (_strikeColor is null)
            {
                try { _strikeColor = File.Exists(StrikeColorFile) && File.ReadAllText(StrikeColorFile).Trim() == Strikes.Black ? Strikes.Black : Strikes.Red; }
                catch { _strikeColor = Strikes.Red; }
                ShowStrikeColor();
            }
            return _strikeColor;
        }
    }

    private void ShowStrikeColor() =>
        StrikeDot.Fill = new SolidColorBrush(_strikeColor == Strikes.Black ? Colors.Black : ColorHelper.FromArgb(255, 212, 0, 0));

    private void StrikeColor_Click(object sender, RoutedEventArgs e)
    {
        _strikeColor = StrikeColor == Strikes.Red ? Strikes.Black : Strikes.Red;
        ShowStrikeColor();
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(StrikeColorFile, _strikeColor); } catch { /* sans importance */ }
        SetStatus($"Couleur du trait : {(_strikeColor == Strikes.Red ? "rouge" : "noir")}.");
    }

    /// <summary>Barrer : la sélection (toute la case sans sélection) ; même bouton pour enlever le trait.</summary>
    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is not { } f || _view is null) return;
        var (start, end) = _view.SelectionFor(f);
        if (end <= start) (start, end) = (0, f.Text.Length);
        bool was = Strikes.IsStruck(f, start, end);
        PushUndo();
        Strikes.Toggle(f, start, end, StrikeColor);
        _view.RefreshStrikes();
        string part = f.Text[start..end].Trim();
        SetStatus($"{(was ? "Trait retiré" : "Barré")} : « {(part.Length > 40 ? part[..40] + "…" : part)} ».");
        ScheduleAutosave();
    }

    /// <summary>« Recalculer » : le total tapé à la main repasse en calcul automatique.</summary>
    private void Recalc_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is not { } f || _view is null) return;
        PushUndo();
        f.Manual = false;
        var changed = RecomputeTotals();
        _view.SyncFields(changed.Append(f));
        UpdateEditBar();
        SetStatus($"Total recalculé : {(f.Text.Length > 0 ? f.Text : "(vide)")}.");
        ScheduleAutosave();
    }

    /// <summary>Couleur du texte : rouge, bleu, vert (noir = enlever) sur la sélection, ou toute la case.</summary>
    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is not { } f || _view is null || sender is not MenuFlyoutItem item) return;
        string color = item.Tag as string ?? "red";
        var (start, end) = _view.SelectionFor(f);
        if (end <= start) (start, end) = (0, f.Text.Length);
        PushUndo();
        bool on = Strikes.SetColor(f, start, end, color);
        if (Strikes.TextColors.ContainsKey(color)) ColorBar.Fill = item.Icon is FontIcon icon ? icon.Foreground : ColorBar.Fill;
        if (f.IsModified) _view.RefreshColors(); else _ = RedrawAsync(_view);
        string part = f.Text[start..end].Trim();
        SetStatus($"{(on ? $"En {item.Text.ToLowerInvariant()}" : "Couleur retirée")} : « {(part.Length > 40 ? part[..40] + "…" : part)} ».");
        _view.Focus(f);
        ScheduleAutosave();
    }

    private void LockToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = LockToggle.IsChecked == true;
        _view?.SetLockMode(on);
        // Pendant le tracé, le doigt dessine au lieu de faire défiler la page.
        PageScroller.HorizontalScrollMode = on ? ScrollMode.Disabled : ScrollMode.Enabled;
        PageScroller.VerticalScrollMode = on ? ScrollMode.Disabled : ScrollMode.Enabled;
        PageScroller.ZoomMode = on ? ZoomMode.Disabled : ZoomMode.Enabled;
        SetStatus(on
            ? "Verrouillage : tracez un rectangle au doigt sur une signature ou un tampon. Touchez une zone orange pour la retirer. Retouchez « Verrouiller » pour terminer."
            : "Verrouillage terminé.");
    }

    // ---------- Lots ----------

    /// <summary>
    /// Bouton « Lots » : la 1re fois, la colonne est créée tout de suite, cases vides (on tape les numéros soi-même).
    /// Ensuite : synchroniser (remplir tout d'un coup), renuméroter, vider, enlever.
    /// </summary>
    private async void Lots_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _project.Pages.Count == 0) return;
        if (!Lots.IsActive(_project))
        {
            PushUndo();
            Lots.CreateEmpty(_project);
            await RefreshViewAsync();
            SetStatus($"Colonne des lots créée : {Lots.ObjectLines(_project).Count()} cases vides. Touchez une case pour taper un numéro (0 = absent : la ligne passe après le dernier lot).");
            ScheduleAutosave();
            return;
        }
        var objects = Lots.ObjectLines(_project).Select(o => o.Line).ToList();
        var numbered = objects.Where(l => l.Lot.Length > 0 && !Lots.IsZero(l)).ToList();
        int zeros = objects.Count(Lots.IsZero);
        string first = numbered.FirstOrDefault()?.Lot ?? "", last = numbered.LastOrDefault()?.Lot ?? "";

        var panel = new StackPanel { Spacing = 12, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = $"{objects.Count} objets : {numbered.Count} numéroté(s){(numbered.Count > 0 ? $" (de {first} à {last})" : "")}"
                 + $"{(zeros > 0 ? $", {zeros} au lot 0" : "")}, {objects.Count - numbered.Count - zeros} case(s) vide(s).",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            Text = "Touchez une case sur la page pour taper un numéro : rien d'autre ne change. 0 = objet absent : sa ligne passe après le dernier lot ; "
                 + "retapez un numéro pour la remettre à sa place. Poubelle : ce n'est pas un objet. « + » : ajouter une case. ⋮⋮ : glisser pour déplacer une rangée.",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            Text = "« Synchroniser automatiquement » remplit toutes les cases d'un coup (les numéros tapés restent, la suite repart d'eux).",
        });
        var frame = new ToggleSwitch
        {
            Header = "Tracer la colonne dans le tableau (traits et fond comme le tableau imprimé)",
            IsOn = _project.LotFrame, OnContent = "Oui", OffContent = "Non",
        };
        bool frameChanged = false;
        frame.Toggled += (_, _) =>
        {
            PushUndo();
            _project.LotFrame = frame.IsOn;
            frameChanged = true;
            ScheduleAutosave();
        };
        panel.Children.Add(frame);
        var start = new NumberBox
        {
            Header = "Renuméroter à partir de", Minimum = 0, SmallChange = 1, Width = 200, HorizontalAlignment = HorizontalAlignment.Left,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = int.TryParse(first, out var f0) ? f0 : 1,
        };
        panel.Children.Add(start);

        ContentDialog? dialog = null;
        string choice = "";
        var extra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var empty = new Button { Content = "Vider les cases", MinHeight = 44 };
        empty.Click += (_, _) => { choice = "empty"; dialog?.Hide(); };
        var clear = new Button { Content = "Enlever la colonne", MinHeight = 44, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 180, 40, 40)) };
        clear.Click += (_, _) => { choice = "clear"; dialog?.Hide(); };
        extra.Children.Add(empty);
        extra.Children.Add(clear);
        panel.Children.Add(extra);

        dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Numéros de lot", Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Synchroniser automatiquement", SecondaryButtonText = "Renuméroter tout",
            CloseButtonText = "Fermer", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (choice == "" && result == ContentDialogResult.Primary) choice = "sync";
        if (choice == "" && result == ContentDialogResult.Secondary) choice = "number";
        switch (choice)
        {
            case "sync":
                PushUndo();
                SyncLots();
                return;
            case "empty":
                if (!await ConfirmAsync("Vider les cases ?", "Tous les numéros seront effacés (les 0 aussi) ; vous les taperez vous-même (bouton ↶ pour revenir en arrière).", "Vider")) break;
                PushUndo();
                Lots.Empty(_project);
                SetStatus($"Cases vidées : {objects.Count} cases à remplir.");
                frameChanged = true;
                break;
            case "number":
                PushUndo();
                Lots.Number(_project, double.IsNaN(start.Value) ? 1 : (int)Math.Round(start.Value));
                var renumbered = Lots.ObjectLines(_project).Where(o => !Lots.IsZero(o.Line)).ToList();
                SetStatus($"Lots renumérotés de {renumbered.FirstOrDefault().Line?.Lot} à {renumbered.LastOrDefault().Line?.Lot}.");
                frameChanged = true;
                break;
            case "clear":
                if (!await ConfirmAsync("Enlever la colonne des lots ?", "Tous les numéros seront retirés (texte et images).", "Enlever")) break;
                PushUndo();
                Lots.Clear(_project);
                SetStatus("Colonne des lots enlevée.");
                frameChanged = true;
                break;
        }
        if (!frameChanged) return;
        Replan();
        RecomputeTotals();
        await RefreshViewAsync();
        ScheduleAutosave();
    }

    // ---------- Retour en arrière ----------

    private readonly List<string> _undo = [];
    private Field? _editSession;

    /// <summary>Mémorise l'état du projet avant une action (30 derniers états).</summary>
    private void PushUndo()
    {
        _undo.Add(System.Text.Json.JsonSerializer.Serialize(_project, ProjectStore.Json));
        if (_undo.Count > 30) _undo.RemoveAt(0);
        UpdateCommands();
    }

    private static string ImageKey(ModelPage p) =>
        System.Text.Json.JsonSerializer.Serialize(new object?[] { p.Crop, p.Skew, p.QuarterTurns, p.Width, PageLayout.BaseHeight(p), p.SourceEntry });

    private void Undo_Click(object sender, RoutedEventArgs e) => _ = UndoAsync();

    private void UndoAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (args.Element is TextBox || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return; // dans une case : Ctrl+Z du texte
        args.Handled = true;
        _ = UndoAsync();
    }

    /// <summary>Bouton ↶ : revient à l'état d'avant la dernière action.</summary>
    private async Task UndoAsync()
    {
        if (_busy || _undo.Count == 0) return;
        string snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        await RunBusyAsync(async () =>
        {
            string? currentId = CurrentPage?.Id;
            var flats = _flats.ToDictionary(kv => kv.Key.Id, kv => (Key: ImageKey(kv.Key), Image: kv.Value));
            var oriented = _oriented.ToDictionary(kv => kv.Key.Id, kv => (Key: kv.Key.QuarterTurns + "|" + kv.Key.SourceEntry, Image: kv.Value));
            _project = System.Text.Json.JsonSerializer.Deserialize<InventoryProject>(snap, ProjectStore.Json) ?? _project;
            _flats.Clear(); _oriented.Clear(); _recent.Clear();
            foreach (var p in _project.Pages)
            {
                if (flats.TryGetValue(p.Id, out var f) && f.Key == ImageKey(p)) Remember(_flats, p, f.Image, 3);
                if (oriented.TryGetValue(p.Id, out var o) && o.Key == p.QuarterTurns + "|" + p.SourceEntry) Remember(_oriented, p, o.Image, 2);
            }
            _editSession = null;
            _currentField = null;
            _crops.Clear();
            int index = Math.Max(0, _project.Pages.FindIndex(p => p.Id == currentId));
            await ShowPageAsync(index, fit: false, keepScroll: index == _index);
            SetStatus("Dernière action annulée.");
            ScheduleAutosave();
        });
    }

    // ---------- Exports ----------

    private void ExportMenu_Opening(object sender, object e)
    {
        int images = _project.Pages.Count(p => p.Source == PageSource.Image);
        ExportAllJpegItem.Visibility = images > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportAllJpegItem.Text = images > 1 ? $"Toutes les images JPEG ({images})" : "Image JPEG";
        ExportOneJpegItem.Visibility = images > 1 && CurrentPage?.Source == PageSource.Image ? Visibility.Visible : Visibility.Collapsed;
        ExportOneJpegItem.Text = $"Image de la page {_index + 1} seulement";
    }

    private async void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count == 0) { SetStatus("Rien à exporter : importez d'abord un document."); return; }
        var picker = new FileSavePicker { SuggestedFileName = _project.Name, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitPicker(picker);
        picker.FileTypeChoices.Add("Texte (colonnes séparées par des tabulations)", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var result = TxtExporter.Write(_project, file.Path);
        SetStatus($"Fichier texte exporté : {file.Name} ({result.LineCount} lignes, UTF-8)."
                  + (Lots.IsActive(_project) ? " La 1re colonne contient le numéro de lot." : ""));
        if (result.Warnings.Count > 0)
            await ShowMessageAsync("À vérifier avant envoi",
                string.Join("\n", result.Warnings.Take(8)) + (result.Warnings.Count > 8 ? $"\n… et {result.Warnings.Count - 8} autre(s)." : ""));
    }

    private async void ExportJpeg_Click(object sender, RoutedEventArgs e) =>
        await ExportJpegAsync(_project.Pages.Where(p => p.Source == PageSource.Image).ToList());

    private async void ExportOneJpeg_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPage is { Source: PageSource.Image } page) await ExportJpegAsync([page]);
    }

    /// <summary>Une image JPEG par photo ou page de PDF : l'original, avec seulement les cases modifiées et les lots.</summary>
    private async Task ExportJpegAsync(List<ModelPage> targets)
    {
        if (targets.Count == 0) { SetStatus("Pas de photo ni de PDF à exporter en image (les TXT, CSV et XLSX s'exportent en .txt)."); return; }
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        await RunBusyAsync(async () =>
        {
            var plan = Replan();
            RecomputeTotals();
            bool frame = _project.LotFrame;
            async Task<Raster?> EditedOf(ModelPage page)
            {
                var baseFlat = _flats.TryGetValue(page, out var cached) ? cached : await RestoreFlatAsync(page);
                return baseFlat is null ? null : ImageExportService.RenderEdits(page, PageLayout.Stretch(baseFlat, page), frame);
            }
            // Lots 0 : leurs morceaux sont pris sur leur page (corrections comprises), puis recollés après le dernier lot.
            var crops = new Dictionary<Arrangement.Unit, Arrangement.Crop?>();
            var unitPages = targets.Select(p => _project.Pages.IndexOf(p))
                .SelectMany(k => plan.Pages[k].Pieces.Where(pc => pc.IsUnit).Select(pc => pc.Unit!.P)).Distinct().ToList();
            for (int i = 0; i < unitPages.Count; i++)
            {
                var page = _project.Pages[unitPages[i]];
                if (page.Source != PageSource.Image) continue;
                SetStatus($"Préparation des lots 0 ({i + 1}/{unitPages.Count})…");
                var edited = await EditedOf(page);
                if (edited is null) continue;
                foreach (var u in plan.Units.Where(u => u.P == unitPages[i])) crops[u] = Arrangement.UnitCrop(u, edited);
            }
            int done = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var page = targets[i];
                int k = _project.Pages.IndexOf(page);
                Progress.Value = (double)i / targets.Count;
                SetStatus($"Création de l'image {i + 1} sur {targets.Count}…");
                var edited = await EditedOf(page);
                if (edited is null) continue;
                var final = edited;
                bool arranged = Arrangement.PageArranged(_project, plan, k);
                if (arranged) final = await Task.Run(() => Arrangement.ComposePage(plan, k, edited, crops));
                // Page qui garde sa forme : la photo d'origine, inchangée sauf dans les cases modifiées.
                if (!arranged && PageLayout.TotalExtra(page) == 0)
                {
                    var source = await GetOrientedSourceAsync(page);
                    if (source is null) continue;
                    final = await Task.Run(() => ImageExporter.ComposeIntoSource(source, page, edited, frame));
                }
                await ImageExportService.SaveJpegAsync(final, System.IO.Path.Combine(folder.Path, ImageExportService.OutputName(page)));
                done++;
            }
            SetStatus($"{done} image(s) JPEG exportée(s) dans « {folder.Name} » (nom d'origine + « - modifié »)."
                      + (plan.Units.Count > 0 && plan.Dest != null ? $" {plan.Units.Count} lot(s) 0 placé(s) après le dernier lot (page {plan.Dest.P + 1})." : ""));
        });
    }

    private async Task<Raster?> GetOrientedSourceAsync(ModelPage page)
    {
        if (_oriented.TryGetValue(page, out var source)) return source;
        if (page.SourceEntry is null || !_sources.TryGetValue(page.SourceEntry, out var bytes)) return null;
        (_, source) = await ImportService.RestoreImageAsync(page, bytes);
        Remember(_oriented, page, source, 2);
        return source;
    }

    /// <summary>Image affichée d'une page enregistrée : recalculée à l'identique, sans relire le texte.</summary>
    private async Task<Raster?> RestoreFlatAsync(ModelPage page)
    {
        if (page.SourceEntry is null || !_sources.TryGetValue(page.SourceEntry, out var bytes)) return null;
        var (image, oriented) = await ImportService.RestoreImageAsync(page, bytes);
        Remember(_oriented, page, oriented, 2);
        return image;
    }

    // ---------- Projet ----------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count == 0) { SetStatus("Rien à enregistrer."); return; }
        var picker = new FileSavePicker { SuggestedFileName = _project.Name, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitPicker(picker);
        picker.FileTypeChoices.Add("Projet d'inventaire", new List<string> { ProjectStore.Extension });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            _projectPath = file.Path;
            RememberProjectPath();
            Autosave();
            SetStatus($"Projet enregistré : {file.Name}. Chaque modification y est maintenant enregistrée automatiquement.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Enregistrement impossible", ex.Message);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitPicker(picker);
        picker.FileTypeFilter.Add(ProjectStore.Extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await RunBusyAsync(async () =>
        {
            await OpenProjectAsync(file.Path, rememberPath: true);
            RememberProjectPath();
            SetStatus($"Projet ouvert : {file.Name} ({_project.Pages.Count} page(s)). Les modifications y sont enregistrées automatiquement.");
            ScheduleAutosave();
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _project.Name, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Nom du projet", Content = box,
            PrimaryButtonText = "Renommer", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
        _project.Name = box.Text.Trim();
        UpdateCommands();
        ScheduleAutosave();
        SetStatus($"Projet renommé : {_project.Name}");
    }

    private async Task OpenProjectAsync(string path, bool rememberPath)
    {
        var (project, sources) = await Task.Run(() => ProjectStore.Load(path));
        ClearAll();
        _project = project;
        _project.Name = Names.Repair(_project.Name);
        foreach (var p in _project.Pages) { p.SourceName = Names.Repair(p.SourceName); RetoucheInventaire.Core.Analysis.PageAnalyzer.Upgrade(p); }
        Lots.Resync(_project);
        _crops.Clear();
        foreach (var (k, v) in sources) _sources[k] = v;
        _projectPath = rememberPath ? path : null;
        await ShowPageAsync(0);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count > 0 && !await ConfirmAsync("Nouveau projet ?", "Les pages actuelles seront fermées. Pensez à enregistrer ou exporter avant.")) return;
        ClearAll();
        _project = new InventoryProject();
        _projectPath = null;
        try { File.Delete(AutosavePath); File.Delete(LastPathFile); } catch { /* sans importance */ }
        SaveState.Text = "";
        UpdateCommands();
        SetStatus("Nouveau projet : touchez « Photo » ou « Importer ».");
    }

    private void ClearAll()
    {
        PagesPanel.Children.Clear();
        _view = null;
        _currentField = null;
        _index = 0;
        _sources.Clear();
        _flats.Clear();
        _oriented.Clear();
        _recent.Clear();
        UpdateCommands();
    }

    private void ScheduleAutosave()
    {
        SaveState.Text = "…";
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>
    /// Enregistrement en continu, une seconde après chaque modification, après chaque page importée et à la fermeture :
    /// dans le fichier projet choisi (dès le premier « Enregistrer ») et dans une copie de secours.
    /// </summary>
    private void Autosave()
    {
        _autosaveTimer.Stop();
        if (_project.Pages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            ProjectStore.Save(AutosavePath, _project, _sources);
            if (_projectPath != null) ProjectStore.Save(_projectPath, _project, _sources);
            ShowSaveState();
        }
        catch (Exception ex)
        {
            SaveState.Text = "⚠ Non enregistré";
            SetStatus($"Enregistrement automatique impossible : {ex.Message}");
        }
    }

    private void ShowSaveState() =>
        SaveState.Text = _projectPath is null
            ? $"✓ {DateTime.Now:HH:mm} · copie de secours"
            : $"✓ {DateTime.Now:HH:mm} · {System.IO.Path.GetFileName(_projectPath)}";

    private void RememberProjectPath()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LastPathFile, _projectPath ?? "");
        }
        catch
        {
            // Sans importance : il suffira de rouvrir le projet.
        }
    }

    // ---------- Recadrage manuel ----------

    private readonly Ellipse[] _handles = new Ellipse[4];
    private PointD[] _corners = new PointD[4];
    private ModelPage? _cropPage;
    private Raster? _cropSource;
    private int _cropTurns;
    private int _dragHandle = -1;

    private void InitCropHandles()
    {
        CropPolygon.Points = new PointCollection();
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            var h = new Ellipse
            {
                Fill = new SolidColorBrush(ColorHelper.FromArgb(160, 61, 139, 255)),
                Stroke = new SolidColorBrush(Colors.White),
            };
            h.PointerPressed += (s, e) => { _dragHandle = index; h.CapturePointer(e.Pointer); e.Handled = true; };
            h.PointerMoved += (s, e) =>
            {
                if (_dragHandle != index || _cropSource is null) return;
                var p = e.GetCurrentPoint(CropCanvas).Position;
                _corners[index] = new PointD(Math.Clamp(p.X, 0, _cropSource.Width - 1), Math.Clamp(p.Y, 0, _cropSource.Height - 1));
                UpdateCropVisual();
            };
            h.PointerReleased += (s, e) => { _dragHandle = -1; h.ReleasePointerCapture(e.Pointer); };
            _handles[i] = h;
            CropCanvas.Children.Add(h);
        }
    }

    private async void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPage is not { Source: PageSource.Image } page) { SetStatus("Le recadrage concerne les photos et les PDF."); return; }
        var source = await GetOrientedSourceAsync(page);
        if (source is null) return;
        _cropPage = page;
        _cropSource = source;
        _cropTurns = 0;
        _corners = (page.Crop ?? Quad.FullImage(source.Width, source.Height)).ToArray();
        ShowCropImage();
        CropOverlay.Visibility = Visibility.Visible;
        SetStatus("Déplacez les 4 poignées sur les coins de la feuille, puis « Valider ».");
    }

    private void ShowCropImage()
    {
        if (_cropSource is null) return;
        // Aperçu réduit pour l'affichage ; les coins restent exprimés en pixels de la source.
        double k = Math.Min(1.0, 1800.0 / Math.Max(_cropSource.Width, _cropSource.Height));
        var preview = k < 1 ? ImageOps.Resize(_cropSource, (int)(_cropSource.Width * k), (int)(_cropSource.Height * k)) : _cropSource;
        CropImage.Source = WinImaging.ToBitmap(preview);
        CropImage.Width = CropCanvas.Width = _cropSource.Width;
        CropImage.Height = CropCanvas.Height = _cropSource.Height;
        UpdateCropVisual();
    }

    private void CropViewbox_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropVisual();

    private void UpdateCropVisual()
    {
        if (_cropSource is null) return;
        double scale = CropViewbox.ActualWidth > 0 ? CropViewbox.ActualWidth / _cropSource.Width : 1;
        if (CropViewbox.ActualHeight > 0) scale = Math.Min(scale, CropViewbox.ActualHeight / _cropSource.Height);
        double radius = 28 / Math.Max(scale, 0.01); // ~56 px à l'écran, quel que soit le zoom
        CropPolygon.StrokeThickness = 3 / Math.Max(scale, 0.01);
        CropPolygon.Points.Clear();
        for (int i = 0; i < 4; i++)
        {
            CropPolygon.Points.Add(new Windows.Foundation.Point(_corners[i].X, _corners[i].Y));
            _handles[i].Width = _handles[i].Height = 2 * radius;
            _handles[i].StrokeThickness = 2 / Math.Max(scale, 0.01);
            Canvas.SetLeft(_handles[i], _corners[i].X - radius);
            Canvas.SetTop(_handles[i], _corners[i].Y - radius);
        }
    }

    private void CropRotate_Click(object sender, RoutedEventArgs e)
    {
        if (_cropSource is null) return;
        _cropSource = ImageOps.Rotate90(_cropSource, 1);
        _cropTurns = (_cropTurns + 1) % 4;
        _corners = PageDetector.Detect(_cropSource).ToArray();
        ShowCropImage();
    }

    private void CropAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_cropSource is null) return;
        _corners = PageDetector.Detect(_cropSource).ToArray();
        UpdateCropVisual();
    }

    private void CropFull_Click(object sender, RoutedEventArgs e)
    {
        if (_cropSource is null) return;
        _corners = Quad.FullImage(_cropSource.Width, _cropSource.Height).ToArray();
        UpdateCropVisual();
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e)
    {
        CropOverlay.Visibility = Visibility.Collapsed;
        _cropPage = null;
        _cropSource = null;
        SetStatus("Recadrage annulé.");
    }

    private async void CropApply_Click(object sender, RoutedEventArgs e)
    {
        if (_cropPage is not { } page || _cropSource is not { } source) return;
        if (page.AllFields().Any(f => f.IsModified)
            && !await ConfirmAsync("Recadrer la page ?", "Le texte de cette page va être relu : ses modifications seront perdues.")) return;

        CropOverlay.Visibility = Visibility.Collapsed;
        var quad = new Quad(_corners[0], _corners[1], _corners[2], _corners[3]);
        int turns = _cropTurns;
        await RunBusyAsync(async () =>
        {
            SetStatus("Redressement et lecture du texte…");
            // Nouvelle page traitée à part, puis échangée : l'ancienne reste intacte pendant le calcul.
            // Les zones verrouillées ne sont pas reprises (elles visaient l'ancien cadrage).
            var fresh = new ModelPage
            {
                Id = page.Id, Source = page.Source, SourceName = page.SourceName, SourceEntry = page.SourceEntry,
                QuarterTurns = (page.QuarterTurns + turns) % 4,
            };
            var (image, _) = await _import.ProcessAsync(fresh, source, quad, (f, text) => { Progress.Value = f; SetStatus($"Recadrage : {text}"); });
            int index = _project.Pages.IndexOf(page);
            if (index < 0) return; // page retirée entre-temps
            PushUndo();
            _project.Pages[index] = fresh;
            Forget(page);
            Remember(_oriented, fresh, source, 2);
            Lots.Resync(_project);
            await ShowPageAsync(index, image);
            SetStatus("Page recadrée et relue. Vérifiez les zones à verrouiller.");
            ScheduleAutosave();
        });
        _cropPage = null;
        _cropSource = null;
    }

    // ---------- Outils d'interface ----------

    private void InitPicker(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateCommands()
    {
        int n = _project.Pages.Count;
        var page = CurrentPage;
        PhotoButton.IsEnabled = ImportButton.IsEnabled = ProjectButton.IsEnabled = !_busy;
        CropButton.IsEnabled = !_busy && page?.Source == PageSource.Image;
        LockToggle.IsEnabled = !_busy && page != null;
        LotsButton.IsEnabled = ExportButton.IsEnabled = EditButton.IsEnabled = !_busy && n > 0;
        SyncItem.IsEnabled = Lots.IsActive(_project);
        RemovePageItem.IsEnabled = page != null;
        UndoButton.IsEnabled = !_busy && _undo.Count > 0;
        SaveItem.IsEnabled = RenameItem.IsEnabled = n > 0;
        Pager.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Content = n > 0 ? $"{_index + 1} / {n}" : "";
        PagePrev.IsEnabled = !_busy && _index > 0;
        PageNext.IsEnabled = !_busy && _index < n - 1;
        PageLabel.IsEnabled = !_busy;
        EmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectName.Text = _project.Name;
        if (_syncButton != null) _syncButton.Visibility = Lots.IsActive(_project) ? Visibility.Visible : Visibility.Collapsed;
        LotsButton.Background = Lots.IsActive(_project)
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 232, 241, 251))
            : new SolidColorBrush(Colors.Transparent);
        UpdateEditBar();
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        UpdateCommands();
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Erreur", ex.Message);
        }
        finally
        {
            _busy = false;
            Progress.Visibility = Visibility.Collapsed;
            UpdateCommands();
        }
    }

    // ---------- Zoom ----------

    /// <summary>Ajuste le zoom pour que la page (et sa colonne de lots) tienne en largeur.</summary>
    private void FitWidthSoon() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (CurrentPage is not { } page || PageScroller.ViewportWidth <= 0) return;
        double width = page.Width + PageView.GutterWidth + 36;
        float zoom = (float)Math.Clamp(PageScroller.ViewportWidth / width, PageScroller.MinZoomFactor, 1.5);
        PageScroller.ChangeView(0, 0, zoom, disableAnimation: true);
    });

    private void Zoom(double factor) =>
        PageScroller.ChangeView(null, null, (float)Math.Clamp(PageScroller.ZoomFactor * factor, PageScroller.MinZoomFactor, PageScroller.MaxZoomFactor));

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.25);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitWidthSoon();

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string ok = "Continuer")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = ok, CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
