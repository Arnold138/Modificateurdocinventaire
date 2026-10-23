using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RetoucheInventaire.App.Services;
using RetoucheInventaire.App.Views;
using RetoucheInventaire.Core;
using RetoucheInventaire.Core.Export;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Storage;
using Windows.Storage.Pickers;
using ModelPage = RetoucheInventaire.Core.Model.Page;

namespace RetoucheInventaire.App;

/// <summary>
/// L'écran unique : importer → corriger sur la page → exporter le .txt.
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
    private readonly Dictionary<string, byte[]> _sources = [];          // entrée d'archive → fichier source
    private readonly Dictionary<string, Raster> _orientedSources = [];  // page → source orientée (pour recadrer)
    private readonly List<PageView> _views = [];
    private readonly Dictionary<string, (StackPanel Panel, TextBlock Title, Border Frame)> _frames = [];
    private PageView? _currentView;
    private Field? _currentField;
    private string? _projectPath;
    private bool _busy;

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
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
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
            try
            {
                await OpenProjectAsync(AutosavePath, rememberPath: false);
                if (File.Exists(LastPathFile)) _projectPath = File.ReadAllText(LastPathFile).Trim() is { Length: > 0 } p ? p : null;
                SetStatus("Travail en cours rechargé." + (_projectPath is null ? "" : $" Enregistrement continu dans {System.IO.Path.GetFileName(_projectPath)}."));
                ShowSaveState();
            }
            catch
            {
                SetStatus("Le travail en cours n'a pas pu être rechargé.");
            }
        }
        UpdateCommands();
    }

    // ---------- Import ----------

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitPicker(picker);
        foreach (var ext in ImportService.Extensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<string>(SetStatus);
            int added = 0;
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                try
                {
                    foreach (var imported in await _import.ImportAsync(file, _sources, progress))
                    {
                        _project.Pages.Add(imported.Page);
                        ShowPage(imported.Page, imported.Image, imported.OrientedSource);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync("Import impossible", $"{file.Name} : {ex.Message}");
                }
            }
            if (_project.Pages.Count == added && files.Count > 0)
                _project.Name = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
            SetStatus($"{added} page(s) importée(s). Touchez une case pour la modifier ; les badges à gauche indiquent titres (T) et totaux (Σ).");
            FitWidthSoon();
            ScheduleAutosave();
        });
    }

    private void ShowPage(ModelPage page, Raster? image, Raster? orientedSource)
    {
        if (orientedSource != null) _orientedSources[page.Id] = orientedSource;
        var view = CreateView(page, image);

        var title = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        var remove = new Button { Content = "Retirer la page", MinHeight = 44, Padding = new Thickness(14, 4, 14, 4) };
        string pageId = page.Id;
        remove.Click += async (_, _) => await RemovePageAsync(pageId);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        header.Children.Add(title);
        header.Children.Add(remove);
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 160, 160, 160)),
            BorderThickness = new Thickness(1),
            Child = view.Root,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        panel.Children.Add(frame);
        _frames[page.Id] = (panel, title, frame);
        PagesPanel.Children.Add(panel);
        EmptyHint.Visibility = Visibility.Collapsed;
        RenumberPages();
        UpdateCommands();
    }

    private PageView CreateView(ModelPage page, Raster? image)
    {
        var view = new PageView(page, image);
        view.FieldFocused += (v, f) => { _currentView = v; _currentField = f; UpdateSelection(); };
        view.Edited += _ => { UpdateSelection(); ScheduleAutosave(); };
        view.Touched += v => { _currentView = v; UpdateCommands(); };
        view.LocksChanged += _ => { ScheduleAutosave(); SetStatus("Zones verrouillées mises à jour."); };
        view.SetLockMode(LockToggle.IsChecked == true);
        _views.Add(view);
        return view;
    }

    private void RenumberPages()
    {
        for (int i = 0; i < _project.Pages.Count; i++)
            if (_frames.TryGetValue(_project.Pages[i].Id, out var f))
                f.Title.Text = $"Page {i + 1}/{_project.Pages.Count} · {_project.Pages[i].SourceName}";
    }

    private async Task RemovePageAsync(string pageId)
    {
        var page = _project.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null || _busy || !await ConfirmAsync("Retirer cette page ?", $"« {page.SourceName} » sera retirée du projet et de l'export.")) return;
        var view = _views.FirstOrDefault(v => v.Page == page);
        if (view != null) _views.Remove(view);
        if (_frames.Remove(page.Id, out var f)) PagesPanel.Children.Remove(f.Panel);
        _project.Pages.Remove(page);
        _orientedSources.Remove(page.Id);
        if (page.SourceEntry != null && _project.Pages.All(p => p.SourceEntry != page.SourceEntry)) _sources.Remove(page.SourceEntry);
        if (_currentView == view) { _currentView = null; _currentField = null; }
        EmptyHint.Visibility = _project.Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenumberPages();
        UpdateSelection();
        ScheduleAutosave();
    }

    // ---------- Édition ----------

    private void UpdateSelection()
    {
        var found = _currentField is null ? null : PageEdits.FindLine(_project, _currentField);
        if (found is { } hit)
        {
            KindButtonText.Text = $"Type : {PageEdits.Label(hit.Line.Kind)}";
            string text = hit.Line.FirstText.Length > 40 ? hit.Line.FirstText[..40] + "…" : hit.Line.FirstText;
            SetStatus($"{hit.Page.SourceName} · « {text} » · {PageEdits.Label(hit.Line.Kind)}"
                      + (_currentField!.IsModified ? $" · original : « {_currentField.OriginalText} »" : ""));
        }
        else KindButtonText.Text = "Type de ligne";
        UpdateCommands();
    }

    private void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is null || _currentView is null) return;
        if (PageEdits.FindLine(_project, _currentField) is not { } hit) return;
        PageEdits.CycleKind(hit.Line);
        var field = _currentField;
        _currentView.Refresh();
        _currentView.Focus(field);
        UpdateSelection();
        ScheduleAutosave();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentField is null || _currentView is null) return;
        _currentView.Revert(_currentField);
        UpdateSelection();
    }

    private void LockToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = LockToggle.IsChecked == true;
        foreach (var v in _views) v.SetLockMode(on);
        // Pendant le tracé, le doigt dessine au lieu de faire défiler la page.
        PageScroller.HorizontalScrollMode = on ? ScrollMode.Disabled : ScrollMode.Enabled;
        PageScroller.VerticalScrollMode = on ? ScrollMode.Disabled : ScrollMode.Enabled;
        PageScroller.ZoomMode = on ? ZoomMode.Disabled : ZoomMode.Enabled;
        SetStatus(on
            ? "Verrouillage : tracez un rectangle au doigt sur une signature ou un tampon. Touchez une zone orange pour la retirer. Retouchez « Verrouiller » pour terminer."
            : "Verrouillage terminé.");
    }

    // ---------- Fichiers ----------

    private async void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count == 0) { SetStatus("Rien à exporter : importez d'abord un document."); return; }
        var picker = new FileSavePicker { SuggestedFileName = _project.Name, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitPicker(picker);
        picker.FileTypeChoices.Add("Texte (colonnes séparées par des tabulations)", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var result = TxtExporter.Write(_project, file.Path);
        SetStatus($"Fichier texte exporté : {file.Name} ({result.LineCount} lignes, UTF-8).");
        if (result.Warnings.Count > 0)
            await ShowMessageAsync("À vérifier avant envoi",
                string.Join("\n", result.Warnings.Take(8)) + (result.Warnings.Count > 8 ? $"\n… et {result.Warnings.Count - 8} autre(s)." : ""));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count == 0) { SetStatus("Rien à enregistrer."); return; }
        var path = _projectPath;
        if (path is null)
        {
            var picker = new FileSavePicker { SuggestedFileName = _project.Name, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitPicker(picker);
            picker.FileTypeChoices.Add("Projet d'inventaire", new List<string> { ProjectStore.Extension });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            path = file.Path;
        }
        try
        {
            _projectPath = path;
            RememberProjectPath();
            Autosave();
            SetStatus($"Projet enregistré : {System.IO.Path.GetFileName(path)}. Chaque modification y est maintenant enregistrée automatiquement.");
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
            SetStatus($"Projet ouvert : {file.Name}. Les modifications y sont enregistrées automatiquement.");
            ScheduleAutosave();
        });
    }

    private async Task OpenProjectAsync(string path, bool rememberPath)
    {
        var (project, sources) = await Task.Run(() => ProjectStore.Load(path));
        ClearAll();
        _project = project;
        foreach (var (k, v) in sources) _sources[k] = v;
        _projectPath = rememberPath ? path : null;
        foreach (var page in _project.Pages)
        {
            Raster? image = null, oriented = null;
            if (page.Source == PageSource.Image && page.SourceEntry != null && _sources.TryGetValue(page.SourceEntry, out var bytes))
                (image, oriented) = await ImportService.RestoreImageAsync(page, bytes);
            ShowPage(page, image, oriented);
        }
        FitWidthSoon();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Pages.Count > 0 && !await ConfirmAsync("Nouveau projet ?", "Les pages actuelles seront fermées. Pensez à enregistrer ou exporter avant.")) return;
        ClearAll();
        _project = new InventoryProject();
        _projectPath = null;
        try { File.Delete(AutosavePath); File.Delete(LastPathFile); } catch { /* sans importance */ }
        SaveState.Text = "";
        SetStatus("Nouveau projet.");
    }

    private void ClearAll()
    {
        foreach (var f in _frames.Values) PagesPanel.Children.Remove(f.Panel);
        _frames.Clear();
        _views.Clear();
        _sources.Clear();
        _orientedSources.Clear();
        _currentView = null;
        _currentField = null;
        EmptyHint.Visibility = Visibility.Visible;
        UpdateSelection();
    }

    private void ScheduleAutosave()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>
    /// Enregistrement en continu, une seconde après chaque modification et à la fermeture :
    /// dans le fichier projet choisi (dès le premier « Enregistrer ») et dans une copie de secours.
    /// </summary>
    private void Autosave()
    {
        if (_project.Pages.Count == 0) return;
        try
        {
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
            ? $"✓ Copie de secours {DateTime.Now:HH:mm:ss} — « Enregistrer » pour choisir le fichier"
            : $"✓ Enregistré {DateTime.Now:HH:mm:ss} · {System.IO.Path.GetFileName(_projectPath)}";

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
        var view = _currentView is { Page.Source: PageSource.Image } ? _currentView : _views.FirstOrDefault(v => v.Page.Source == PageSource.Image);
        if (view is null) { SetStatus("Le recadrage concerne les photos et les PDF. Touchez d'abord la page voulue."); return; }
        var page = view.Page;
        if (!_orientedSources.TryGetValue(page.Id, out var source))
        {
            if (page.SourceEntry is null || !_sources.TryGetValue(page.SourceEntry, out var bytes)) return;
            (_, source) = await ImportService.RestoreImageAsync(page, bytes);
            _orientedSources[page.Id] = source;
        }
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
            var (image, _) = await _import.ProcessAsync(fresh, source, quad);
            int index = _project.Pages.IndexOf(page);
            if (index < 0) return; // page retirée entre-temps
            _project.Pages[index] = fresh;
            _orientedSources[fresh.Id] = source;

            var old = _views.First(v => v.Page == page);
            var view = CreateView(fresh, image);
            _views.Remove(old);
            _frames[fresh.Id].Frame.Child = view.Root;
            if (_currentView == old) { _currentView = view; _currentField = null; }
            UpdateSelection();
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
        bool any = _project.Pages.Count > 0;
        CropButton.IsEnabled = !_busy && _project.Pages.Any(p => p.Source == PageSource.Image);
        LockToggle.IsEnabled = !_busy && any;
        KindButton.IsEnabled = !_busy && _currentField != null;
        RevertButton.IsEnabled = !_busy && _currentField?.IsModified == true;
        ExportButton.IsEnabled = SaveButton.IsEnabled = !_busy && any;
        ImportButton.IsEnabled = OpenButton.IsEnabled = NewButton.IsEnabled = !_busy;
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        Busy.IsActive = true;
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
            Busy.IsActive = false;
            UpdateCommands();
        }
    }

    /// <summary>Ajuste le zoom pour que la page tienne en largeur.</summary>
    private void FitWidthSoon() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (_project.Pages.Count == 0 || PageScroller.ViewportWidth <= 0) return;
        double width = _project.Pages.Max(p => p.Width) + 48;
        float zoom = (float)Math.Clamp(PageScroller.ViewportWidth / width, PageScroller.MinZoomFactor, 1.5);
        PageScroller.ChangeView(0, null, zoom, disableAnimation: true);
    });

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

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Continuer", CloseButtonText = "Annuler", DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
