using Microsoft.UI.Xaml;

namespace RetoucheInventaire.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // Dernier filet : une erreur imprévue ne ferme pas l'app ni ne la fige ; elle est notée et l'écran est réaffiché.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            (_window as MainWindow)?.OnUnexpectedError(e.Exception);
        };
    }

    /// <summary>
    /// Une seule fenêtre à la fois : deux fenêtres ouvertes écrivaient chacune leur version dans la même copie de secours
    /// (le travail de l'une écrasait celui de l'autre). La 2e fenêtre le dit et se ferme sans rien enregistrer.
    /// </summary>
    private static System.Threading.Mutex? _single;
    public static bool AlreadyRunning { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _single = new System.Threading.Mutex(true, "RetoucheInventaire-une-seule-fenetre", out bool first);
        AlreadyRunning = !first;
        _window = new MainWindow();
        _window.Activate();
    }
}
