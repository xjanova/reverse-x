using System.Windows;
using ReverseX.App.Services;
using ReverseX.App.ViewModels;
using ReverseX.Core;
using ReverseX.Core.Projects;

namespace ReverseX.App;

public partial class App : Application
{
    private async Task RunScreenshotsAsync(string dir, bool empty)
    {
        try { await ScreenshotMode.RunAsync(dir, empty); }
        catch (Exception ex) { AppPaths.Log("Screenshot mode failed", ex); Console.Error.WriteLine(ex); }
        Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log("Unhandled UI exception", args.Exception);
            MessageBox.Show(Loc.Get("Err.Generic"), "Reverse X", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var prefs = UiPrefs.Load();
        Loc.Apply(prefs.Language, save: false);
        ThemeManager.Apply(prefs.Theme, save: false);

        var shot = Array.IndexOf(e.Args, "--screenshots");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            var dir = e.Args[shot + 1];
            var empty = e.Args.Contains("--empty");
            _ = RunScreenshotsAsync(dir, empty);
            return;
        }

        var vm = new MainViewModel(new ProjectStore());
        new MainWindow(vm).Show();
        _ = vm.Home.CheckToolsAsync();
    }
}
