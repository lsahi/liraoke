using System.Configuration;
using System.Data;
using System.Windows;

namespace KaraokeFilmMaker.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 0) { return; }
        string? projectPath;
        try { projectPath = WorkspaceStartupArguments.Resolve(e.Args); }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StartupUri = null;
        var window = new MainWindow();
        MainWindow = window;
        RoutedEventHandler? loaded = null;
        loaded = async (_, _) =>
        {
            window.Loaded -= loaded;
            await window.OpenWorkspaceEntryAsync(projectPath!);
        };
        window.Loaded += loaded;
        window.Show();
    }
}

