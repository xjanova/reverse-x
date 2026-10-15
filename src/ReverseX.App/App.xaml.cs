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

    private async Task RunPhase5Async(string dir)
    {
        try { await ScreenshotMode.RunPhase5Async(dir); }
        catch (Exception ex) { AppPaths.Log("Phase 5 screenshot mode failed", ex); Console.Error.WriteLine(ex); }
        Shutdown();
    }

    private async Task RunPhase4Async(string dir, string[] files)
    {
        try { await ScreenshotMode.RunPhase4Async(dir, files); }
        catch (Exception ex) { AppPaths.Log("Phase 4 screenshot mode failed", ex); Console.Error.WriteLine(ex); }
        Shutdown();
    }

    private async Task RunPhase8Async(string dir, string[] files)
    {
        try { await ScreenshotMode.RunPhase8Async(dir, files); }
        catch (Exception ex) { AppPaths.Log("Phase 8 screenshot mode failed", ex); Console.Error.WriteLine(ex); }
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
        // Background-thread crashes end the process; at least leave the reason in the log.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log($"Unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.Log("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var prefs = UiPrefs.Load();
        Loc.Apply(prefs.Language, save: false);
        ThemeManager.Apply(prefs.Theme, save: false);

        var shot = Array.IndexOf(e.Args, "--screenshots");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            var dir = e.Args[shot + 1];
            var empty = e.Args.Contains("--empty");
            if (e.Args.Contains("--phase5")) { _ = RunPhase5Async(dir); return; }
            var p8 = Array.IndexOf(e.Args, "--phase8");
            if (p8 >= 0)
            {
                _ = RunPhase8Async(dir, e.Args.Skip(p8 + 1).TakeWhile(a => !a.StartsWith("--")).ToArray());
                return;
            }
            var p4 = Array.IndexOf(e.Args, "--phase4");
            if (p4 >= 0)
            {
                // --phase4 <dotnet.dll> <app.asar|folder> <native.exe>: real analyses of the given files (read-only)
                var files = e.Args.Skip(p4 + 1).TakeWhile(a => !a.StartsWith("--")).ToArray();
                _ = RunPhase4Async(dir, files);
                return;
            }
            _ = RunScreenshotsAsync(dir, empty);
            return;
        }

        var vm = new MainViewModel(new ProjectStore());
        new MainWindow(vm).Show();
        _ = vm.Home.CheckToolsAsync();
    }
}
