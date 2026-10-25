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

    private static readonly string LoupeFile = System.IO.Path.Combine(DataDir, "loupe.txt");

    /// <summary>Mise en page › Loupe : activée ou non, gardé sur cet ordinateur.</summary>
    private void Loupe_Click(object sender, RoutedEventArgs e)
    {
        PageView.LoupeEnabled = LoupeItem.IsChecked;
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(LoupeFile, LoupeItem.IsChecked ? "oui" : "non"); } catch { /* sans importance */ }
        SetStatus(LoupeItem.IsChecked ? "Loupe activée." : "Loupe désactivée : plus d'agrandissement au-dessus de la case.");
    }

    private async Task StartAsync()
    {
        try { PageView.LoupeEnabled = !(File.Exists(LoupeFile) && File.ReadAllText(LoupeFile).Trim() == "non"); } catch { /* réglage par défaut */ }
        LoupeItem.IsChecked = PageView.LoupeEnabled;
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

    /// <summary>Mise en page : lots 0 recollés à la fin, lignes supprimées, rangées déplacées à la main.</summary>
    private Arrangement.Plan _plan = new([], [], [], null, [], 0);
    /// <summary>Morceaux d'image des lots 0 (pour recomposer la page où ils arrivent sans rouvrir leur page).</summary>
    private readonly Dictionary<string, Arrangement.Crop?> _crops = [];

    /// <summary>Recalcule la mise en page ; crée ou retire les pages « Lots de fin » au besoin (on reste sur la même page).</summary>
    private Arrangement.Plan Replan()
    {
        var cur = CurrentPage;
        if (Arrangement.SyncEndPages(_project))
        {
            int i = cur is null ? -1 : _project.Pages.IndexOf(cur);
            _index = i >= 0 ? i : Math.Clamp(_index, 0, Math.Max(0, _project.Pages.Count - 1));
            _crops.Clear();
            UpdateCommands();
        }
        return _plan = Arrangement.Build(_project);
    }

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
        // Page « Lots de fin » : composée sur l'image de sa page modèle.
        var page = _project.Pages[k];
        int? tpl = k < _plan.Pages.Count ? _plan.Pages[k].Template : null;
        var own = page.EndLots ? (tpl is int t && t >= 0 ? await OwnDisplayAsync(_project.Pages[t]) : null) : await OwnDisplayAsync(page);
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
        if (_project.Pages.Count == 0) { _index = 0; UpdateCommands(); return; }
        if (!_project.Pages.Contains(page)) page = _project.Pages[Math.Clamp(_index, 0, _project.Pages.Count - 1)];
        _index = _project.Pages.IndexOf(page);
        if (page.Source == PageSource.Image && !_flats.ContainsKey(page)) SetStatus($"Ouverture de la page {_index + 1}…");
        int k = _index;
        var display = await DisplayOfAsync(k);
        if (CurrentPage != page) return; // l'utilisateur a déjà changé de page

        var title = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7,
                                    Text = $"Page {_index + 1} sur {_project.Pages.Count} · {page.SourceName}{(page.EndLots ? " (lots 0, créée automatiquement)" : "")}" };
        var remove = new Button { Content = "Retirer…", MinHeight = 40, Padding = new Thickness(14, 4, 14, 4), CornerRadius = new CornerRadius(20) };
        ToolTipService.SetToolTip(remove, "Retirer cette page, des pages au choix ou tout le dossier");
        remove.Click += async (_, _) => await RetireMenuAsync(page);
        var totals = new Button { MinHeight = 40, Padding = new Thickness(14, 4, 14, 4), CornerRadius = new CornerRadius(20) };
        var totalsContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        totalsContent.Children.Add(new TextBlock { Text = "Σ", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        totalsContent.Children.Add(new TextBlock { Text = "Totaux" });
        totals.Content = totalsContent;
        ToolTipService.SetToolTip(totals, "Recalculer les totaux de la page, ajouter une ligne TOTAL");
        totals.Click += async (_, _) => await TotalsMenuAsync(page);
        var sync = new Button
        {
            MinHeight = 40, Padding = new Thickness(14, 4, 14, 4), CornerRadius = new CornerRadius(20),
            Style = (Style)Application.Current.Resources["PillButton"],
            Visibility = Lots.IsActive(_project) ? Visibility.Visible : Visibility.Collapsed,
        };
        var syncContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        syncContent.Children.Add(new FontIcon { Glyph = "", FontSize = 15 });
        syncContent.Children.Add(new TextBlock { Text = "Synchroniser…" });
        ToolTipService.SetToolTip(sync, "Remplir les cases vides de cette page ou de toutes les pages (les numéros déjà là ne bougent pas)");
        sync.Content = syncContent;
        sync.Click += async (_, _) => await SyncDialogAsync();
        _syncButton = sync;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(PageView.GutterWidth, 0, 0, 0) };
        header.Children.Add(title);
        header.Children.Add(sync);
        if (!page.EndLots)
        {
            header.Children.Add(totals);
            header.Children.Add(remove);
        }

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
        view.LotChanged += async (v, line, value) => await ApplyLotAsync(v, line, value);
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
        view.LineMoved += async (v, table, line, before) => await MoveLineAsync(v.Page, table, line, before);
        view.UnitMoved += async (v, table, unit, before) => await PlaceZeroAsync(v.Page, table, unit, before);
        view.LineCommands = LineCommandsFor;
        view.SetLockMode(LockToggle.IsChecked == true);
        view.SetMoveMode(MoveToggle.IsChecked == true);
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

    /// <summary>« Synchroniser… » : cette page ou toutes les pages.</summary>
    private async Task SyncDialogAsync()
    {
        if (_busy || !Lots.IsActive(_project)) return;
        var page = CurrentPage;
        var options = new List<(string, string, bool)>();
        if (page is { EndLots: false }) options.Add(($"Cette page (page {_index + 1})", "page", false));
        options.Add(("Toutes les pages", "all", false));
        var choice = await ChooseAsync("Synchroniser les lots",
            "Remplit les cases vides. Les numéros déjà là ne bougent pas : une case vide prend le numéro qui suit et décale seulement les lots qui la suivent directement (6, 7, 8 → 7, 8, 9). Un ordre fait à la main (30, 29, 6…) est gardé. Pour tout renuméroter dans l'ordre affiché : Lots › Renuméroter tout.",
            [.. options]);
        if (choice is null) return;
        PushUndo();
        SyncLots(choice == "page" && page != null ? new List<ModelPage> { page } : null);
    }

    /// <summary>Remplit les cases vides (pages choisies, ou toutes) sans toucher aux numéros déjà là ; dit ce qui a changé.</summary>
    private void SyncLots(List<ModelPage>? only = null)
    {
        var before = Lots.ObjectLines(_project).ToDictionary(o => o.Line, o => o.Line.Lot);
        Lots.FillAutomatically(_project, only);
        Replan();
        RecomputeTotals();
        _ = RefreshViewAsync();
        var objs = Lots.ObjectLines(_project).ToList();
        int filled = objs.Count(o => (before.GetValueOrDefault(o.Line) ?? "").Length == 0 && o.Line.Lot.Length > 0);
        int shifted = objs.Count(o => (before.GetValueOrDefault(o.Line) ?? "").Length > 0 && before[o.Line] != o.Line.Lot);
        string where = only != null ? $" sur la page {string.Join(", ", only.Select(p => _project.Pages.IndexOf(p) + 1))}" : "";
        // Numéros en double (ex. après un ordre fait à la main 30, 29 puis une case vide → 30) : signalés, pas corrigés en douce.
        var dup = objs.Where(o => o.Line.Lot.Length > 0 && !Lots.IsZero(o.Line)).GroupBy(o => o.Line.Lot).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        string warn = dup.Count > 0 ? $" Attention, numéro(s) en double : {string.Join(", ", dup.Take(6))}{(dup.Count > 6 ? "…" : "")} (touchez la case pour corriger)." : "";
        SetStatus((objs.Count == 0 ? "Aucun objet à numéroter."
            : filled == 0 ? $"Rien à remplir{where} : toutes les cases ont un numéro (rien n'a bougé)."
            : $"{filled} case(s) remplie(s){where}{(shifted > 0 ? $", {shifted} lot(s) suivant(s) décalé(s) d'un cran" : "")} ; les autres numéros n'ont pas bougé. ↶ pour annuler.") + warn);
        ScheduleAutosave();
    }

    private async void SyncLots_Click(object sender, RoutedEventArgs e) => await SyncDialogAsync();

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
        KindButton.IsEnabled = StrikeButton.IsEnabled = ColorButton.IsEnabled = LineButton.IsEnabled = !_busy;
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

    // ---------- Lignes ajoutées : menu « Ligne » (bouton, clic droit, appui long) ----------

    private static string Short(string t) => t.Length > 40 ? t[..40] + "…" : t;

    /// <summary>Ligne de texte (dans la désignation tapée sur plusieurs lignes) où se trouve le curseur.</summary>
    private int CaretLine(Field f)
    {
        if (f.Column != 0 || !f.Text.Contains('\n') || _view is null) return 0;
        int start = Math.Clamp(_view.SelectionFor(f).Start, 0, f.Text.Length);
        return f.Text[..start].Count(c => c == '\n');
    }

    /// <summary>Ce que le menu « Ligne » propose pour la case touchée.</summary>
    private List<PageView.LineCommand> LineCommandsFor(Field f)
    {
        var list = new List<PageView.LineCommand>();
        if (_busy || PageEdits.FindLine(_project, f) is not { } hit) return list;
        var (page, line) = hit;
        if (_plan.Moving.Any(u => u.Lines.Contains(line)))
        {
            // Ligne au lot 0, affichée à la fin : la remettre, ou la supprimer.
            list.Add(new("Remettre à sa place", "La ligne retourne où elle était (case de lot vide : tapez son numéro).", "\uE7A7",
                () => _ = LineActionAsync("back", 0, f)));
            list.Add(new("Supprimer cette ligne", "Elle disparaît de la vente (page, .txt, totaux).", "\uE74D",
                () => _ = LineActionAsync("delete", 0, f), Danger: true));
            return list;
        }
        int typed = CaretLine(f);
        string rest = typed > 0 ? string.Join(" ", f.Text.Replace("\r\n", "\n").Split('\n').Skip(typed)).Trim() : "";
        bool canCopy = Rows.CanCopy(page, line);
        if (canCopy)
        {
            list.Add(new("Ajouter une ligne vide en dessous", "Rangée complète (case de lot, désignation, prix) sous cette rangée. La suite descend.", "\uE710",
                () => _ = LineActionAsync("blankBelow", 0, f)));
            if (Rows.Locate(page, line) is { R: > 0 })
                list.Add(new("Ajouter une ligne vide au-dessus", "Rangée complète (case de lot, désignation, prix) au-dessus de cette rangée.", "\uE710",
                    () => _ = LineActionAsync("blankAbove", 0, f)));
            list.Add(new("Dupliquer la ligne", "Copie juste en dessous (texte et prix), à corriger. La suite descend.", "\uE8C8",
                () => _ = LineActionAsync("copy", 0, f)));
        }
        if (rest.Length > 0 && canCopy)
            list.Add(new("Séparer ici", $"« {Short(rest)} » passe dans sa propre ligne, juste en dessous.", "\uE8C6",
                () => _ = LineActionAsync("typed", typed, f)));
        else if (Rows.CanSplit(page, line))
            list.Add(new("Séparer au-dessus (tracer un trait)", "Un trait au-dessus de cette ligne : elle devient une rangée à part, avec sa case de lot et ses prix.", "\uE8C6",
                () => _ = LineActionAsync("split", 0, f)));
        if (Rows.CanSplitBelow(page, line))
            list.Add(new("Séparer en dessous (tracer un trait)", "Un trait sous cette ligne : ce qui suit devient une rangée à part.", "\uE8C6",
                () => _ = LineActionAsync("splitBelow", 0, f)));
        if (Lots.HasCell(line) && !Lots.IsZero(line))
            list.Add(new("Mettre à la fin (lot 0)", "Objet absent : sa ligne part à la fin, ce qui suit remonte.", "\uE74B",
                () => _ = LineActionAsync("end", 0, f)));
        if (Lots.IsActive(_project) && Rows.Locate(page, line) is { } at0 && !at0.Table.LotsOff && !Lots.HasCell(line) && line.FirstText.Length > 0)
            list.Add(new("Ajouter une case de lot", "La case manque sur cet objet : elle apparaît vide (tapez le numéro ou « Synchroniser »).", "\uE710",
                () => _ = LineActionAsync("addLot", 0, f)));
        if (Rows.RowTrimOf(page, line) != null)
            list.Add(new("Réduire la rangée (enlever l'espace vide)", "La rangée prend la hauteur des autres ; ce qui suit remonte.", "\uE73F",
                () => _ = LineActionAsync("trim", 0, f)));
        else if (Rows.TrimsOf(page, line).Count > 0)
            list.Add(new("Remettre la hauteur d'origine", "L'espace vide imprimé revient.", "\uE740",
                () => _ = LineActionAsync("untrim", 0, f)));
        if (line.TotalScope is { } ts)
            list.Add(new(ts.Bold ? "Enlever le gras" : "Mettre en gras", "Ligne TOTAL ajoutée : écriture en gras ou comme les autres lignes.", "\uE8DD",
                () => _ = LineActionAsync("bold", 0, f)));
        if (Rows.AddedRowOf(page, line) is { } added)
        {
            if (added.Band.Kind == "Split" && added.At.Row.Lines.Count > 0 && added.At.Row.Lines[0] == line)
                list.Add(new("Remettre dans la rangée du dessus", "Annule « Séparer ici » : le trait ajouté disparaît.", "\uE74A",
                    () => _ = LineActionAsync("merge", 0, f)));
            if (added.Band.Kind == "Copy")
                list.Add(new("Supprimer cette ligne ajoutée", "La ligne copiée disparaît, la suite remonte.", "\uE74D",
                    () => _ = LineActionAsync("remove", 0, f), Danger: true));
        }
        if (Rows.AddedRowOf(page, line) is not { Band.Kind: "Copy" } && Rows.Locate(page, line) != null && line.FirstText.Length > 0)
            list.Add(new("Supprimer cette ligne", "Objet retiré de la vente : la ligne (et sa suite) disparaît, ce qui suit remonte.", "\uE74D",
                () => _ = LineActionAsync("delete", 0, f), Danger: true));
        return list;
    }

    /// <summary>Bouton « Ligne » : mêmes actions que le clic droit, pour la case sélectionnée.</summary>
    private void LineMenu_Opening(object sender, object e)
    {
        LineMenu.Items.Clear();
        var commands = _currentField is null ? new List<PageView.LineCommand>() : LineCommandsFor(_currentField);
        foreach (var c in commands)
        {
            var item = new MenuFlyoutItem { Text = c.Label, Icon = new FontIcon { Glyph = c.Glyph } };
            if (c.Danger) item.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 180, 40, 40));
            ToolTipService.SetToolTip(item, c.Hint);
            item.Click += (_, _) => c.Run();
            LineMenu.Items.Add(item);
        }
        if (commands.Count == 0)
            LineMenu.Items.Add(new MenuFlyoutItem { Text = "Rien à faire ici : choisissez une ligne d'article du tableau", IsEnabled = false });
    }

    /// <summary>Exécute une action du menu « Ligne » ; une seule action à annuler (↶).</summary>
    private async Task LineActionAsync(string act, int from, Field f)
    {
        if (_busy || PageEdits.FindLine(_project, f) is not { } hit) return;
        var (page, line) = hit;
        string name = Short(line.FirstText);
        if (act == "end") { if (_view != null) await ApplyLotAsync(_view, line, "0"); return; }
        if (act == "addLot")
        {
            PushUndo();
            Lots.Add(_project, line);
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            SetStatus($"Case de lot ajoutée sur « {name} » : tapez le numéro, ou « Synchroniser… » (les autres numéros ne bougent pas).");
            ScheduleAutosave();
            return;
        }
        if (act is "trim" or "untrim")
        {
            PushUndo();
            if (act == "trim") Rows.TrimRow(page, line); else Rows.UntrimRow(page, line);
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            _view?.FlashLine(line);
            SetStatus(act == "trim" ? $"Rangée de « {name} » réduite : même hauteur que les autres. ↶ pour annuler." : $"Rangée de « {name} » remise à sa hauteur d'origine.");
            ScheduleAutosave();
            return;
        }
        if (act == "bold" && line.TotalScope is { } scope)
        {
            PushUndo();
            scope.Bold = !scope.Bold;
            await RefreshViewAsync();
            SetStatus(scope.Bold ? $"« {name} » en gras." : $"« {name} » : même écriture que les autres lignes.");
            ScheduleAutosave();
            return;
        }
        if (act == "back")
        {
            PushUndo();
            Lots.Set(_project, line, "");
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            SetStatus($"« {name} » est revenu à sa place : tapez son numéro (ou « Synchroniser »).");
            ScheduleAutosave();
            return;
        }
        if (act == "delete")
        {
            if (!await ConfirmAsync("Supprimer cette ligne ?", $"« {name} » (et sa suite) disparaît de la page, du fichier texte et des totaux. Les autres numéros de lot ne changent pas. ↶ ou Retirer… › Remettre les lignes supprimées pour la retrouver.", "Supprimer")) return;
            PushUndo();
            line.Deleted = true;
            Lots.Resync(_project);
            Replan();
            RecomputeTotals();
            _currentField = null;
            await RefreshViewAsync();
            UpdateEditBar();
            SetStatus($"« {name} » supprimée : ce qui suit est remonté. ↶ pour annuler.");
            ScheduleAutosave();
            return;
        }
        PushUndo();
        PrintedLine? show = null;
        string msg = "";
        // Rangée haute (espace vide imprimé) : réduite avant d'ajouter ou de séparer, pour que tout ait la hauteur des autres.
        var loc = Rows.Locate(page, line);
        PrintedLine? prev = loc is { I: > 0 } ? loc.Row.Lines[loc.I - 1] : null;
        PrintedLine? next = loc != null && loc.I + 1 < loc.Row.Lines.Count ? loc.Row.Lines[loc.I + 1] : null;
        if (act is "copy" or "blankBelow" or "blankAbove" or "typed" && Rows.CanCopy(page, line)) Rows.TrimRow(page, line);
        switch (act)
        {
            case "blankBelow":
            case "blankAbove":
                show = Rows.AddBlank(page, line, above: act == "blankAbove");
                msg = $"Ligne vide ajoutée {(act == "blankAbove" ? "au-dessus de" : "sous")} « {name} » : tapez la désignation et les prix. Case de lot vide : tapez le numéro ou « Synchroniser ».";
                break;
            case "copy":
                show = Rows.Copy(page, line);
                if (show != null) PageLayout.SetExtra(page, show, PageView.LineExtra(show));
                msg = $"« {name} » dupliquée juste en dessous : corrigez la quantité et le prix. Case de lot vide : tapez le numéro ou « Synchroniser ».";
                break;
            case "typed":
                show = Rows.SplitTypedText(page, line, from);
                if (show != null)
                {
                    PageLayout.SetExtra(page, line, PageView.LineExtra(line));
                    PageLayout.SetExtra(page, show, PageView.LineExtra(show));
                }
                msg = $"« {Short((show ?? line).FirstText)} » est maintenant sur sa propre ligne : tapez ses prix et son numéro de lot.";
                break;
            case "split":
                show = Rows.Split(page, line) ? line : null;
                msg = $"« {name} » est maintenant une rangée à part, avec sa case de lot et ses prix.";
                break;
            case "splitBelow":
                show = Rows.SplitBelow(page, line) ? line : null;
                msg = $"Trait tracé sous « {name} » : ce qui suit est une rangée à part.";
                break;
            case "merge":
                Rows.RemoveAdded(page, line);
                msg = $"« {name} » est remise dans la rangée du dessus.";
                break;
            case "remove":
                Rows.RemoveAdded(page, line);
                msg = "Ligne ajoutée supprimée, la suite est remontée.";
                break;
        }
        if ((act is "copy" or "typed" or "split" or "splitBelow" or "blankBelow" or "blankAbove") && show is null)
        {
            _undo.RemoveAt(_undo.Count - 1);
            UpdateCommands();
            SetStatus("Impossible ici : choisissez une ligne d'article d'un tableau.");
            return;
        }
        if (act == "split" && prev != null) { Rows.TrimRow(page, prev); Rows.TrimRow(page, line); }
        if (act == "splitBelow" && next != null) { Rows.TrimRow(page, line); Rows.TrimRow(page, next); }
        Lots.Resync(_project);
        Replan();
        RecomputeTotals();
        _editSession = null;
        await RefreshViewAsync();
        if (show != null && _view != null)
        {
            _view.FlashLine(show);
            var target = act == "split" ? f : show.Cells[0];
            if (_view.Shows(target)) { _currentField = target; _view.Focus(target); }
        }
        UpdateEditBar();
        SetStatus(msg + " ↶ pour annuler.");
        ScheduleAutosave();
    }

    // ---------- Lots 0 : à la fin du document ----------

    /// <summary>Où est maintenant la ligne : « page 9 « Lots de fin », 3e ligne » ou « page 7, après le dernier lot (« … ») ».</summary>
    private (int K, string Text)? PlaceOf(PrintedLine line)
    {
        var u = _plan.Moving.FirstOrDefault(x => x.Lines.Contains(line));
        if (u is null) return null;
        int k = u.EndPage >= 0 ? u.EndPage : u.Dest.P;
        int rank = _plan.Pages[k].Pieces.Where(pc => pc.IsUnit).ToList().FindIndex(pc => pc.Unit == u) + 1;
        if (_project.Pages[k].EndLots) return (k, $"page {k + 1} « Lots de fin », {(rank == 1 ? "1re" : rank + "e")} ligne");
        var anchor = u.Dest.Table.Rows[u.Dest.R].Lines.FirstOrDefault(l => l.FirstText.Length > 0) ?? u.Object;
        return (k, $"page {k + 1}, après « {Short(anchor.FirstText)} » {(u.Object.EndAt != null ? "(endroit choisi)" : "(fin de sa partie, avant la ligne TOTAL)")}");
    }

    /// <summary>Choix à cocher avec un titre en gras et une explication.</summary>
    private static RadioButton Choice(string group, string title, string? detail, bool chosen, UIElement? extra = null)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail != null) content.Children.Add(new TextBlock { Text = detail, FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        if (extra != null) content.Children.Add(extra);
        return new RadioButton { GroupName = group, Content = content, IsChecked = chosen, MinHeight = 44, Margin = new Thickness(0, 2, 0, 2) };
    }

    /// <summary>
    /// Lot 0 : la 1re fois, on demande où mettre les lots à la fin (après le dernier lot, ou sur une page « Lots de fin »
    /// placée après la page choisie). Le choix vaut pour les suivants (modifiable dans Lots › Lots de fin…). Null : annulé.
    /// </summary>
    private async Task<EndLotsChoice?> ChooseEndPlaceAsync(PrintedLine? line, bool force = false)
    {
        if (_project.EndLots != null && !force) return _project.EndLots;
        var cur = _project.EndLots;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        if (line != null)
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Inlines =
                {
                    new Microsoft.UI.Xaml.Documents.Run { Text = $"« {Short(line.FirstText)} »", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new Microsoft.UI.Xaml.Documents.Run { Text = " n'est plus dans la vente (lot 0) : sa ligne quitte sa place, ce qui suit remonte, et elle passe à la fin." },
                },
            });
        panel.Children.Add(new TextBlock { Text = "Où mettre les lots à la fin ?", TextWrapping = TextWrapping.Wrap });
        var after = new ComboBox { MinWidth = 300, Margin = new Thickness(0, 4, 0, 4) };
        var ids = new List<string>();
        string wanted = cur?.AfterId ?? CurrentPage?.Id ?? "";
        for (int i = 0; i < _project.Pages.Count; i++)
        {
            var pg = _project.Pages[i];
            if (pg.EndLots) continue;
            ids.Add(pg.Id);
            after.Items.Add($"après la page {i + 1} · {Short(pg.SourceName)}");
            if (pg.Id == wanted) after.SelectedIndex = ids.Count - 1;
        }
        if (after.SelectedIndex < 0 && ids.Count > 0) after.SelectedIndex = ids.Count - 1;
        var last = Choice("endMode", "À la fin de sa partie", "Après le dernier lot, juste avant la ligne TOTAL de sa partie (ex. « TOTAL MOBILIER ET MATERIEL »). Pour le mettre ailleurs : Modifier, puis glisser sa poignée.", cur?.Mode != "page");
        var pageBox = new StackPanel { Spacing = 2 };
        pageBox.Children.Add(new TextBlock { Text = "Une page créée exprès, même mise en page que la vente, placée :", FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        pageBox.Children.Add(after);
        pageBox.Children.Add(new TextBlock { Text = "Les pages suivantes sont renumérotées ; une nouvelle page s'ajoute quand elle est pleine.", FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var dedicated = Choice("endMode", "Sur une page « Lots de fin »", null, cur?.Mode == "page", pageBox);
        after.SelectionChanged += (_, _) => dedicated.IsChecked = true;
        panel.Children.Add(last);
        panel.Children.Add(dedicated);
        panel.Children.Add(new TextBlock
        {
            Text = "Ce choix vaut pour tous les lots 0 (modifiable : Lots › Lots de fin…). Pour remettre un lot à sa place : retapez son numéro.",
            FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = line != null ? "Mettre ce lot à la fin ?" : "Lots de fin : où ?",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = line != null ? "Oui, à la fin" : "OK", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        bool page = dedicated.IsChecked == true && after.SelectedIndex >= 0;
        return new EndLotsChoice { Mode = page ? "page" : "last", AfterId = page ? ids[after.SelectedIndex] : null };
    }

    /// <summary>Numéro tapé dans une case de lot (0 : à la fin, après confirmation).</summary>
    private async Task ApplyLotAsync(PageView v, PrintedLine line, string value)
    {
        bool wasZero = Lots.IsZero(line);
        bool goingZero = System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), "^[+-]?0+$") && !wasZero;
        EndLotsChoice? end = null;
        if (goingZero)
        {
            end = await ChooseEndPlaceAsync(line);
            if (end is null) { v.UpdateLots(); SetStatus("Lot 0 annulé : la ligne reste à sa place."); return; }
        }
        PushUndo();
        if (end != null) _project.EndLots = end;
        int before = _project.Pages.Count;
        string sig = LotColumn.Signature(v.Page, _project.LotFrame);
        Lots.Set(_project, line, value);
        bool zero = Lots.IsZero(line);
        Replan();
        RecomputeTotals();
        if (zero != wasZero || before != _project.Pages.Count || sig != LotColumn.Signature(v.Page, _project.LotFrame)) await RefreshViewAsync();
        else v.UpdateLots();
        if (zero && !wasZero)
        {
            int added = _project.Pages.Count - before;
            SetStatus(PlaceOf(line) is { } where
                ? $"Lot 0 : « {Short(line.FirstText)} » est maintenant à la fin : {where.Text}."
                  + (added > 0 ? $" Page « Lots de fin » ajoutée (page {where.K + 1}) : les pages suivantes ont été renumérotées." : "")
                  + " Pour le placer ailleurs : Modifier, puis glissez sa poignée orange ⋮⋮. Pour le remettre : retapez son numéro, ou clic droit › Remettre à sa place."
                : "Lot 0 : aucun autre lot numéroté, la ligne reste en place.");
        }
        else if (wasZero && !zero) SetStatus($"Lot {(line.Lot.Length > 0 ? line.Lot : "(vide)")} : « {Short(line.FirstText)} » est revenu à sa place.");
        else SetStatus(line.Lot.Length > 0 ? $"Lot {line.Lot} enregistré." : "Case de lot vidée.");
        ScheduleAutosave();
    }

    /// <summary>
    /// Poignée d'un lot 0 recollé (mode Modifier) : il est posé avant la rangée <paramref name="before"/> (null : à la fin du
    /// tableau) ; l'endroit est gardé (EndAt) tant qu'il est au lot 0.
    /// </summary>
    private async Task PlaceZeroAsync(ModelPage page, Table table, Arrangement.Unit unit, int? before)
    {
        if (_busy) return;
        var order = Lots.RowOrder(table);
        int i = before is int b ? order.IndexOf(b) : order.Count;
        var anchorRow = table.Rows[order[Math.Max(0, i - 1)]];
        var anchor = anchorRow.Lines.FirstOrDefault(l => l.Cells.Count > 0);
        if (anchor is null) return;
        PushUndo();
        unit.Object.EndAt = new EndAnchor { Page = page.Id, Line = anchor.Cells[0].Id };
        Replan();
        RecomputeTotals();
        await RefreshViewAsync();
        _view?.FlashLine(unit.Object);
        SetStatus($"Lot 0 « {Short(unit.Object.FirstText)} » placé après « {Short(anchor.FirstText.Length > 0 ? anchor.FirstText : "(rangée)")} ». Il reste là ; retaper son numéro le remet à sa place d'origine. ↶ pour annuler.");
        ScheduleAutosave();
    }

    /// <summary>
    /// Poignée d'une ligne dans un bloc : la ligne devient une rangée à part (trait au-dessus et, s'il le faut, en dessous ;
    /// rangées réduites à la hauteur des autres), puis cette rangée est posée avant la rangée <paramref name="before"/> (null : à la fin).
    /// </summary>
    private async Task MoveLineAsync(ModelPage page, Table table, PrintedLine line, int? before)
    {
        if (_busy || Rows.Locate(page, line) is not { } at) return;
        PushUndo();
        var target = before is int b ? table.Rows[b] : null;
        var prev = at.I > 0 ? at.Row.Lines[at.I - 1] : null;
        if (prev != null) Rows.Split(page, line);
        var own = Rows.Locate(page, line)!;
        var next = own.I + 1 < own.Row.Lines.Count ? own.Row.Lines[own.I + 1] : null;
        if (next != null) Rows.SplitBelow(page, line);
        foreach (var l in new[] { prev, line, next }) if (l != null) Rows.TrimRow(page, l);
        int from = table.Rows.FindIndex(r => r.Lines.Contains(line));
        int to = target is null ? -1 : table.Rows.IndexOf(target);
        if (to != from)
        {
            var order = Lots.RowOrder(table).Where(r => r != from).ToList();
            order.Insert(to < 0 ? order.Count : order.IndexOf(to), from);
            table.RowOrder = order;
        }
        Lots.Resync(_project);
        Replan();
        RecomputeTotals();
        await RefreshViewAsync();
        _view?.FlashLine(line);
        SetStatus($"« {Short(line.FirstText)} » déplacée : c'est maintenant une rangée à part (case de lot vide : tapez son numéro ou « Synchroniser »). ↶ pour annuler.");
        ScheduleAutosave();
    }

    // ---------- Page : retirer, totaux ----------

    /// <summary>Cases à cocher des pages (hors pages « lots de fin »).</summary>
    private (StackPanel Panel, List<(CheckBox Box, ModelPage Page)> Boxes) PageChecklist(Func<ModelPage, bool> isChecked)
    {
        var panel = new StackPanel { Spacing = 2 };
        var boxes = new List<(CheckBox, ModelPage)>();
        for (int i = 0; i < _project.Pages.Count; i++)
        {
            var pg = _project.Pages[i];
            if (pg.EndLots) continue;
            var box = new CheckBox
            {
                MinHeight = 40, IsChecked = isChecked(pg),
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new Microsoft.UI.Xaml.Documents.Run { Text = $"Page {i + 1}  ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new Microsoft.UI.Xaml.Documents.Run { Text = Short(pg.SourceName) },
                    },
                },
            };
            panel.Children.Add(box);
            boxes.Add((box, pg));
        }
        return (panel, boxes);
    }

    /// <summary>Fenêtre à boutons empilés (un choix par bouton) ; renvoie la valeur du bouton touché, ou null.</summary>
    private async Task<string?> ChooseAsync(string title, string message, params (string Label, string Value, bool Danger)[] options)
    {
        string? choice = null;
        ContentDialog? dialog = null;
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var (label, value, danger) in options)
        {
            var b = new Button { Content = label, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (danger) b.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 180, 40, 40));
            b.Click += (_, _) => { choice = value; dialog?.Hide(); };
            panel.Children.Add(b);
        }
        dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = panel, CloseButtonText = "Annuler" };
        await dialog.ShowAsync();
        return choice;
    }

    /// <summary>« Retirer… » : cette page, des pages au choix, tout le dossier ; remettre les lignes supprimées.</summary>
    private async Task RetireMenuAsync(ModelPage page)
    {
        if (_busy) return;
        int k = _project.Pages.IndexOf(page);
        var deleted = page.AllLines().Where(l => l.Deleted).ToList();
        var options = new List<(string, string, bool)> { ($"Cette page (page {k + 1})", "one", true), ("Pages au choix…", "some", false), ("Tout le dossier", "all", true) };
        if (deleted.Count > 0) options.Add(($"Remettre les lignes supprimées ({deleted.Count})", "restore", false));
        var choice = await ChooseAsync("Retirer", "Que voulez-vous retirer ? (↶ pour revenir en arrière)", [.. options]);
        if (choice == "one") { await RemovePageAsync(page); return; }
        if (choice == "restore")
        {
            PushUndo();
            foreach (var l in deleted) l.Deleted = false;
            Lots.Resync(_project);
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            SetStatus($"{deleted.Count} ligne(s) remise(s) à leur place.");
            ScheduleAutosave();
            return;
        }
        List<ModelPage> targets;
        if (choice == "all")
        {
            if (!await ConfirmAsync("Retirer tout le dossier ?", $"Les {_project.Pages.Count} pages seront retirées (projet vide). ↶ pour revenir en arrière.", "Tout retirer")) return;
            targets = [.. _project.Pages];
        }
        else if (choice == "some")
        {
            var (list, boxes) = PageChecklist(pg => pg == page);
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Cochez les pages à retirer.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 440 });
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "Retirer des pages", Content = panel,
                PrimaryButtonText = "Retirer", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            targets = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Page).ToList();
            if (targets.Count == 0) { SetStatus("Aucune page cochée."); return; }
            if (!await ConfirmAsync("Retirer ces pages ?", $"{targets.Count} page(s) : {string.Join(", ", targets.Select(pg => _project.Pages.IndexOf(pg) + 1))}. ↶ pour revenir en arrière.", "Retirer")) return;
        }
        else return;
        PushUndo();
        foreach (var pg in targets)
        {
            _project.Pages.Remove(pg);
            Forget(pg);
            if (pg.SourceEntry != null && _project.Pages.All(x => x.SourceEntry != pg.SourceEntry)) _sources.Remove(pg.SourceEntry);
        }
        if (_project.Pages.All(x => x.EndLots)) _project.Pages.Clear();
        Lots.Resync(_project);
        _crops.Clear();
        Replan();
        RecomputeTotals();
        await ShowPageAsync(Math.Min(k, _project.Pages.Count - 1));
        SetStatus($"{targets.Count} page(s) retirée(s).");
        ScheduleAutosave();
    }

    /// <summary>« Totaux » : recalculer les totaux de la page ; ajouter une ligne TOTAL (calcul au choix).</summary>
    private async Task TotalsMenuAsync(ModelPage page)
    {
        if (_busy) return;
        var choice = await ChooseAsync("Totaux", "Les totaux se recalculent tout seuls quand un montant change. Ici, vous pouvez aussi :",
            ("Ajouter une ligne TOTAL…", "add", false), ("Recalculer les totaux de la page", "recalc", false));
        if (choice == "recalc")
        {
            PushUndo();
            int n = 0;
            foreach (var l in page.AllLines())
                foreach (var f in l.Cells)
                    if (Totals.IsComputedCell(l, f) && f.Manual) { f.Manual = false; n++; }
            Replan();
            RecomputeTotals();
            await RefreshViewAsync();
            SetStatus($"Totaux de la page recalculés{(n > 0 ? $" ({n} total(aux) tapé(s) à la main remis en automatique)" : "")}.");
            ScheduleAutosave();
            return;
        }
        if (choice != "add") return;
        var hit = _currentField is null ? null : PageEdits.FindLine(_project, _currentField);
        var here = hit is { } h && h.Page == page && Rows.CanCopy(page, h.Line) ? h.Line : null;
        // Tableaux possibles : ceux qui ont des lignes d'objet ; proposé : celui de la case touchée, sinon le 1er avec des montants.
        var tables = page.Tables.Select(t => (T: t, Last: Rows.LastObjectLine(page, t))).Where(x => x.Last != null).ToList();
        if (tables.Count == 0) { SetStatus("Pas de tableau d'objets sur cette page pour y ajouter une ligne TOTAL."); return; }
        static bool HasAmounts(Table t) => t.Rows.Any(r => r.Lines.Any(l => l.Cells.Skip(1).Any(c => c.Text.Any(char.IsDigit))));
        int pick = Math.Max(0, tables.FindIndex(x => HasAmounts(x.T)));

        var panel = new StackPanel { Spacing = 6, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = "La ligne TOTAL s'ajoute comme les autres lignes du tableau.", TextWrapping = TextWrapping.Wrap });
        var where = new ComboBox { MinWidth = 300, Header = "Sous" };
        if (here != null) where.Items.Add($"la ligne touchée « {Short(here.FirstText)} »");
        for (int i = 0; i < tables.Count; i++) where.Items.Add($"la fin du tableau {i + 1} (« {Short(tables[i].Last!.FirstText)} »)");
        where.SelectedIndex = here != null ? 0 : pick;
        if (tables.Count > 1 || here != null) panel.Children.Add(where);
        else panel.Children.Add(new TextBlock { Text = $"Sous « {Short(tables[0].Last!.FirstText)} » (fin du tableau).", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Elle additionne :", Margin = new Thickness(0, 6, 0, 0) });
        var above = Choice("totScope", "Les lignes au-dessus", "Du début du tableau (ou du total précédent) jusqu'à elle.", true);
        var onPage = Choice("totScope", "Toute cette page", null, false);
        var (checklist, boxes) = PageChecklist(pg => pg == page);
        var pagesList = new ScrollViewer { Content = checklist, MaxHeight = 220, Margin = new Thickness(28, 0, 0, 0), Visibility = Visibility.Collapsed };
        var some = Choice("totScope", "Les pages au choix", "À cocher dans la liste qui s'ouvre.", false);
        var all = Choice("totScope", "Tout le document", null, false);
        foreach (var r in new[] { above, onPage, some, all }) r.Checked += (_, _) => pagesList.Visibility = some.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(above);
        panel.Children.Add(onPage);
        panel.Children.Add(some);
        panel.Children.Add(pagesList);
        panel.Children.Add(all);
        var auto = new CheckBox { Content = "Calculer automatiquement (sinon : à taper soi-même)", IsChecked = true, MinHeight = 40 };
        panel.Children.Add(auto);
        var bold = new CheckBox { Content = "En gras (sinon : même écriture que les autres lignes)", IsChecked = false, MinHeight = 40 };
        panel.Children.Add(bold);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Ajouter une ligne TOTAL", Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = "Ajouter", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        int sel = where.SelectedIndex < 0 ? 0 : where.SelectedIndex;
        var anchor = here != null && sel == 0 ? here : tables[Math.Clamp(here != null ? sel - 1 : sel, 0, tables.Count - 1)].Last!;
        string mode = onPage.IsChecked == true ? "page" : some.IsChecked == true ? "pages" : all.IsChecked == true ? "all" : "above";
        var scope = new TotalScope
        {
            Mode = mode, Auto = auto.IsChecked == true, Bold = bold.IsChecked == true,
            Pages = mode == "pages" ? boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Page.Id).ToList() : [],
        };
        PushUndo();
        Rows.TrimRow(page, anchor);
        var t = Rows.AddTotalLine(page, anchor, scope);
        if (t is null)
        {
            _undo.RemoveAt(_undo.Count - 1);
            UpdateCommands();
            SetStatus("Impossible d'ajouter la ligne ici.");
            return;
        }
        Lots.Resync(_project);
        Replan();
        RecomputeTotals();
        await RefreshViewAsync();
        _view?.FlashLine(t);
        string sums = string.Join(" / ", t.Cells.Skip(1).Select(c => c.Text).Where(x => x.Length > 0));
        SetStatus($"Ligne TOTAL ajoutée sous « {Short(anchor.FirstText)} »"
                  + (scope.Auto ? $" : {(sums.Length > 0 ? sums : "0")}. Elle se recalcule toute seule" : " : tapez les montants")
                  + ". Touchez « TOTAL » pour renommer (ex. « TOTAL SALLE 2 »). ↶ pour annuler.");
        ScheduleAutosave();
    }

    /// <summary>Aide : les numéros de lot, pas à pas.</summary>
    private async Task LotsHelpAsync()
    {
        var steps = new (string Title, string Text)[]
        {
            ("Choisir les pages.", "Bouton Lots : cochez les pages (et tableaux) qui reçoivent des numéros, puis OK. Une page de garde, un récapitulatif ou une signature restent sans numéro. Pour changer : Lots › Pages et tableaux…"),
            ("Numéroter.", "Touchez une case à gauche d'un objet et tapez son numéro : rien d'autre ne change. Ou « Synchroniser… » (cette page ou toutes les pages) : les cases vides se remplissent ; les numéros déjà là ne bougent pas, une case vide prend le numéro qui suit et décale seulement les lots qui la suivent directement. Case qui manque sur un objet : clic droit › Ajouter une case de lot. Pour tout refaire dans l'ordre affiché : Renuméroter tout."),
            ("Objet absent : à la fin.", "Tapez 0 (ou clic droit › Mettre à la fin). La 1re fois, l'app demande où : à la fin de sa partie (juste avant sa ligne TOTAL, ex. « TOTAL MOBILIER ET MATERIEL »), ou sur une page « Lots de fin » placée où vous voulez (les pages suivantes sont renumérotées, une nouvelle page s'ajoute quand elle est pleine). Le message en bas dit exactement où la ligne est partie. Pour la mettre ailleurs : Modifier, puis glissez sa poignée orange ⋮⋮. Pour la remettre : retapez son numéro (ou clic droit › Remettre à sa place)."),
            ("Objet retiré de la vente.", "Clic droit › Supprimer cette ligne : elle disparaît (page, fichier texte, totaux), ce qui suit remonte. Pour la retrouver : ↶, ou Retirer… › Remettre les lignes supprimées."),
            ("Ajouter ou séparer.", "Clic droit (ou bouton Ligne) : ligne vide au-dessus ou en dessous, dupliquer, tracer un trait au-dessus ou en dessous pour séparer un gros bloc. Une rangée trop haute (espace vide imprimé) est réduite à la hauteur des autres ; aussi au clic droit : Réduire la rangée."),
            ("Déplacer.", "Touchez Modifier en haut : les poignées ⋮⋮ apparaissent à gauche de chaque ligne (la poubelle se cache). Glissez-en une vers le haut ou le bas : une rangée, ou une ligne seule d'un bloc (poignée blanche), qui devient alors une rangée. Retouchez Modifier pour terminer."),
            ("Exporter.", "Images JPEG, document PDF (tout le document ou la page affichée) ou fichier texte ; les totaux sont recalculés juste avant."),
            ("Totaux.", "Ils suivent tout seuls. Bouton Totaux de la page : recalculer, ou ajouter une ligne TOTAL (lignes au-dessus, la page, des pages au choix ou tout le document ; en gras ou non)."),
            ("Mise en page.", "Cochez ou non la loupe « Original » au-dessus de la case."),
        };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 560 };
        for (int i = 0; i < steps.Length; i++)
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Inlines =
                {
                    new Microsoft.UI.Xaml.Documents.Run { Text = $"{i + 1}. {steps[i].Title} ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new Microsoft.UI.Xaml.Documents.Run { Text = steps[i].Text },
                },
            });
        panel.Children.Add(new TextBlock { Text = "Rien ne change sans votre action. ↶ annule la dernière action (jusqu'à 30).", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Numéros de lot : comment ça marche ?",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 }, CloseButtonText = "Compris", DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
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

    /// <summary>« Modifier » : poignées ⋮⋮ sur toutes les rangées (et sur chaque ligne d'un bloc), tout de suite ; la poubelle et le « + » s'effacent.</summary>
    private void MoveToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = MoveToggle.IsChecked == true;
        _view?.SetMoveMode(on);
        SetStatus(on
            ? "Modifier : glissez une poignée ⋮⋮ vers le haut ou le bas pour déplacer la rangée (poignée blanche : une ligne seule d'un bloc). Retouchez « Modifier » pour terminer."
            : "Modifier terminé.");
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
    /// <summary>« la page 4 » / « les pages 4, 5, 6 » : pages qui ont des cases de lot.</summary>
    private string PagesWithLots()
    {
        var pages = Lots.ObjectLines(_project).Select(o => _project.Pages.IndexOf(o.Page) + 1).Distinct().ToList();
        return (pages.Count > 1 ? "les pages " : "la page ") + string.Join(", ", pages);
    }

    /// <summary>
    /// Pages (et tableaux) qui reçoivent les lots : une case à cocher par page, et par tableau quand la page en a plusieurs.
    /// Proposé : les tableaux qui contiennent des objets (pas une page de garde, une signature, un récapitulatif).
    /// Renvoie les tableaux cochés, ou null (Annuler).
    /// </summary>
    private async Task<HashSet<Table>?> ChooseLotTablesAsync()
    {
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock { Text = "Cochez les pages qui reçoivent des numéros de lot. Les autres pages ne changent pas.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var boxes = new List<(CheckBox Box, Table Table)>();
        for (int i = 0; i < _project.Pages.Count; i++)
        {
            var tables = Lots.LotTables(_project.Pages[i]);
            if (tables.Count == 0)
            {
                list.Children.Add(new CheckBox { Content = $"Page {i + 1} — pas de tableau", IsEnabled = false, MinHeight = 40 });
                continue;
            }
            var pageBox = new CheckBox
            {
                MinHeight = 40,
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new Microsoft.UI.Xaml.Documents.Run { Text = $"Page {i + 1}  ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new Microsoft.UI.Xaml.Documents.Run { Text = Short(tables[0].Name) + (tables.Count > 1 ? $" · {tables.Count} tableaux" : "") },
                    },
                },
                IsChecked = tables.All(x => Lots.DefaultChoice(_project, x.Table)),
            };
            list.Children.Add(pageBox);
            if (tables.Count == 1) { boxes.Add((pageBox, tables[0].Table)); continue; }
            var subs = new List<CheckBox>();
            foreach (var x in tables)
            {
                var sub = new CheckBox
                {
                    Content = Short(x.Name) + (x.Objects ? "" : " (sans objet)"), MinHeight = 36, Margin = new Thickness(32, 0, 0, 0),
                    IsChecked = Lots.DefaultChoice(_project, x.Table),
                };
                subs.Add(sub);
                boxes.Add((sub, x.Table));
                list.Children.Add(sub);
            }
            bool syncing = false;
            pageBox.Click += (_, _) => { syncing = true; foreach (var c in subs) c.IsChecked = pageBox.IsChecked == true; syncing = false; };
            foreach (var c in subs) c.Click += (_, _) => { if (!syncing) pageBox.IsChecked = subs.Any(x => x.IsChecked == true); };
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Lots : quelles pages ?",
            Content = new ScrollViewer { Content = list, MaxHeight = 520 },
            PrimaryButtonText = "OK", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Table).ToHashSet();
    }

    private async void Lots_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _project.Pages.Count == 0) return;
        if (!Lots.IsActive(_project))
        {
            var chosen = await ChooseLotTablesAsync();
            if (chosen is null) return;
            if (chosen.Count == 0) { SetStatus("Aucune page cochée : pas de numéros de lot."); return; }
            PushUndo();
            Lots.SetTables(_project, chosen);
            Lots.CreateEmpty(_project);
            Replan();
            await RefreshViewAsync();
            SetStatus($"Colonne des lots créée sur {PagesWithLots()} : {Lots.ObjectLines(_project).Count()} cases vides. Touchez une case pour taper un numéro (0 = absent : la ligne passe à la fin). Aide : Lots › Comment ça marche ?");
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
            Text = "Touchez une case sur la page pour taper un numéro : rien d'autre ne change. 0 = objet absent : sa ligne passe à la fin (après le dernier lot ou sur une page « Lots de fin ») ; "
                 + "retapez un numéro pour la remettre à sa place. Poubelle : ce n'est pas un objet. « + » : ajouter une case. ⋮⋮ : glisser pour déplacer une rangée.",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            Text = "« Synchroniser… » remplit les cases vides (cette page ou toutes) sans toucher aux numéros déjà là. « Renuméroter tout » refait toute la numérotation dans l'ordre affiché.",
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
        var pagesButton = new Button { Content = "Pages et tableaux…", MinHeight = 44 };
        pagesButton.Click += (_, _) => { choice = "pages"; dialog?.Hide(); };
        extra.Children.Add(pagesButton);
        var endButton = new Button { Content = "Lots de fin (lot 0)…", MinHeight = 44 };
        endButton.Click += (_, _) => { choice = "end"; dialog?.Hide(); };
        var helpButton = new Button { Content = "Comment ça marche ?", MinHeight = 44 };
        helpButton.Click += (_, _) => { choice = "help"; dialog?.Hide(); };
        var extra2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        extra2.Children.Add(endButton);
        extra2.Children.Add(helpButton);
        panel.Children.Add(extra2);
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
            PrimaryButtonText = "Synchroniser…", SecondaryButtonText = "Renuméroter tout",
            CloseButtonText = "Fermer", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (choice == "" && result == ContentDialogResult.Primary) choice = "sync";
        if (choice == "" && result == ContentDialogResult.Secondary) choice = "number";
        switch (choice)
        {
            case "sync":
                await SyncDialogAsync();
                return;
            case "help":
                await LotsHelpAsync();
                break;
            case "end":
                var endChoice = await ChooseEndPlaceAsync(null, force: true);
                if (endChoice is null) break;
                PushUndo();
                _project.EndLots = endChoice;
                Replan();
                RecomputeTotals();
                await RefreshViewAsync();
                var ends = _project.Pages.Select((pg, i) => pg.EndLots ? i + 1 : 0).Where(i => i > 0).ToList();
                SetStatus(endChoice.Mode == "page"
                    ? $"Lots 0 sur une page « Lots de fin »{(ends.Count > 0 ? $" (page {string.Join(", ", ends)})" : " (créée au premier lot 0)")}."
                    : "Lots 0 à la fin de leur partie, juste avant la ligne TOTAL (Modifier : les glisser ailleurs).");
                ScheduleAutosave();
                return;
            case "pages":
                var chosen = await ChooseLotTablesAsync();
                if (chosen is null) break;
                PushUndo();
                Lots.SetTables(_project, chosen);
                if (!Lots.IsActive(_project) && chosen.Count > 0) Lots.CreateEmpty(_project);
                SetStatus(Lots.ObjectLines(_project).Any() ? $"Lots sur {PagesWithLots()} (les numéros déjà tapés restent)." : "Plus aucune page avec des lots.");
                frameChanged = true;
                break;
            case "empty":
                if (!await ConfirmAsync("Vider les cases ?", "Tous les numéros seront effacés (les 0 aussi) ; vous les taperez vous-même (bouton ↶ pour revenir en arrière).", "Vider")) break;
                PushUndo();
                Lots.Empty(_project);
                SetStatus($"Cases vidées : {objects.Count} cases à remplir.");
                frameChanged = true;
                break;
            case "number":
                if (!await ConfirmAsync("Renuméroter tout ?", "Tous les numéros (tapés ou synchronisés) sont refaits dans l'ordre affiché, sauf les 0. Pour seulement remplir les cases vides : Synchroniser… (↶ pour revenir en arrière).", "Renuméroter")) break;
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
        ExportPdfItem.Visibility = images > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportPdfItem.Text = images > 1 ? $"Document PDF ({images} pages)" : "Document PDF";
        ExportOnePdfItem.Visibility = images > 1 && CurrentPage?.Source == PageSource.Image ? Visibility.Visible : Visibility.Collapsed;
        ExportOnePdfItem.Text = $"PDF de la page {_index + 1} seulement";
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e) =>
        await ExportJpegAsync(_project.Pages.Where(p => p.Source == PageSource.Image).ToList(), pdf: true);

    private async void ExportOnePdf_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPage is { Source: PageSource.Image } page) await ExportJpegAsync([page], pdf: true);
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
    /// <summary>
    /// Export des pages (photos et PDF), totaux recalculés avant : chaque page telle qu'à l'écran (corrections, lots, lots 0,
    /// pages « lots de fin », lignes ajoutées). <paramref name="pdf"/> : un seul fichier PDF (une page par page), sinon un JPEG par page.
    /// </summary>
    private async Task ExportJpegAsync(List<ModelPage> targets, bool pdf = false)
    {
        if (targets.Count == 0) { SetStatus("Pas de photo ni de PDF à exporter (les TXT, CSV et XLSX s'exportent en .txt)."); return; }
        StorageFolder? folder = null;
        StorageFile? pdfFile = null;
        if (pdf)
        {
            var save = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = _project.Name + (targets.Count == 1 && _project.Pages.Count(p => p.Source == PageSource.Image) > 1 ? $" - page {_project.Pages.IndexOf(targets[0]) + 1}" : "") + " - modifié",
            };
            save.FileTypeChoices.Add("Document PDF", new List<string> { ".pdf" });
            InitPicker(save);
            pdfFile = await save.PickSaveFileAsync();
            if (pdfFile is null) return;
        }
        else
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitPicker(picker);
            folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
        }
        var jpegs = new List<byte[]>();

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
                .SelectMany(k => plan.Pages[k].Pieces.Where(pc => pc.IsUnit).Select(pc => pc.Unit!.P)).Distinct().Where(p => !_project.Pages[p].EndLots).ToList();
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
                // Page « lots de fin » : composée sur l'image de sa page modèle.
                int? tpl = plan.Pages[k].Template;
                var edited = page.EndLots ? (tpl is int t && t >= 0 ? await EditedOf(_project.Pages[t]) : null) : await EditedOf(page);
                if (edited is null) continue;
                var final = edited;
                bool arranged = Arrangement.PageArranged(_project, plan, k);
                if (arranged) final = await Task.Run(() => Arrangement.ComposePage(plan, k, edited, crops));
                // Page qui garde sa forme : la photo d'origine, inchangée sauf dans les cases modifiées.
                if (!arranged && PageLayout.TotalExtra(page) == 0 && page.Inserts.Count == 0)
                {
                    var source = await GetOrientedSourceAsync(page);
                    if (source is null) continue;
                    final = await Task.Run(() => ImageExporter.ComposeIntoSource(source, page, edited, frame));
                }
                if (pdf) jpegs.Add(await ImageExportService.EncodeJpegAsync(final));
                else await ImageExportService.SaveJpegAsync(final, System.IO.Path.Combine(folder!.Path, ImageExportService.OutputName(page)));
                done++;
            }
            if (pdf)
            {
                SetStatus("Création du PDF…");
                var bytes = await Task.Run(() => PdfWriter.Build(jpegs, _project.Name));
                await FileIO.WriteBytesAsync(pdfFile!, bytes);
            }
            int skipped = pdf && targets.Count > 1 ? _project.Pages.Count(p => p.Source != PageSource.Image) : 0;
            SetStatus((pdf
                          ? $"PDF exporté : « {pdfFile!.Name} », {done} page(s), chacune telle qu'à l'écran (totaux recalculés)."
                            + (skipped > 0 ? $" {skipped} page(s) TXT/CSV/XLSX non comprise(s) (elles s'exportent en .txt)." : "")
                          : $"{done} image(s) JPEG exportée(s) dans « {folder!.Name} » (nom d'origine + « - modifié »).")
                      + (plan.Moving.Count > 0 && plan.Dest != null
                          ? $" {plan.Moving.Count} lot(s) 0 placé(s) {(_project.Pages[plan.Dest.P].EndLots ? "sur la page « Lots de fin »" : "après le dernier lot")} (page {plan.Dest.P + 1})." : ""));
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
            && !await ConfirmAsync("Recadrer la page ?", "Le texte de cette page va être relu. Les numéros de lot et les corrections des lignes retrouvées sont gardés ; les lignes ajoutées sont perdues.")) return;

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
            int kept = PageEdits.CarryOver(page, fresh);
            foreach (var l in fresh.AllLines().ToList()) PageLayout.SetExtra(fresh, l, PageView.LineExtra(l)); // désignations sur plusieurs lignes
            _project.Pages[index] = fresh;
            Forget(page);
            Remember(_oriented, fresh, source, 2);
            Lots.Resync(_project);
            Replan();
            RecomputeTotals();
            await ShowPageAsync(index, image);
            SetStatus($"Page recadrée et relue : {kept} ligne(s) retrouvée(s) avec leur numéro de lot et leurs corrections. Vérifiez les zones à verrouiller.");
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
        CropButton.IsEnabled = !_busy && page?.Source == PageSource.Image && !page.EndLots;
        LockToggle.IsEnabled = !_busy && page != null && !page.EndLots;
        MoveToggle.IsEnabled = !_busy && page != null && !page.EndLots;
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
