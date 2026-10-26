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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
