using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReverseX.App.ViewModels;
using ReverseX.Core;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Projects;

namespace ReverseX.App.Services;

/// <summary>
/// "ReverseX.exe --screenshots DIR": renders the real main window to PNG for night and sky (no mouse, no desktop capture),
/// then exits. Uses a throw-away projects.json so the user's own list is untouched.
/// </summary>
internal static class ScreenshotMode
{
    public static async Task RunAsync(string dir, bool empty)
    {
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(Path.GetTempPath(), "rx-shot-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new ProjectStore(tmp);
        if (!empty)
        {
            var now = DateTime.UtcNow;
            var asm = typeof(ProjectStore).Assembly.Location;
            var folder = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var samples = new[] { (asm, 5), (Path.Combine(folder, "ReverseX.exe"), 40), (folder, 130), (Path.Combine(folder, "ReverseX.deps.json"), 600) };
            foreach (var (p, mins) in samples)
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                var size = File.Exists(p) ? new FileInfo(p).Length : 0;
                store.Add(p, FileKindDetector.Detect(p), size, now.AddMinutes(-mins));
            }
        }

        var vm = new MainViewModel(store);
        var win = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowInTaskbar = false, Width = 1280, Height = 960,
        };
        win.Show();
        await vm.Home.CheckToolsAsync();
        await Flush();

        foreach (var theme in new[] { UiPrefs.Night, UiPrefs.Sky })
        {
            ThemeManager.Apply(theme, save: false);
            await Flush();
            var name = $"phase3-home-{(theme == UiPrefs.Sky ? "sky" : "night")}{(empty ? "-empty" : "")}.png";
            Save(win, Path.Combine(dir, name));
        }
        win.Close();
        try { File.Delete(tmp); } catch (IOException) { }
    }

    /// <summary>Real REA analyses of the given files, rendered in both themes. files = [.NET dll, asar/app folder, native exe].</summary>
    public static async Task RunPhase4Async(string dir, string[] files)
    {
        Directory.CreateDirectory(dir);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "rx-shot4-" + Guid.NewGuid().ToString("N"));
        var store = new ProjectStore(Path.Combine(tmpRoot, "projects.json"));
        Directory.CreateDirectory(tmpRoot);
        var vm = new MainViewModel(store, snapshots: new SnapshotStore(Path.Combine(tmpRoot, "projects")));
        var win = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowInTaskbar = false, Width = 1280, Height = 800,
        };
        win.Show();
        await Flush();

        async Task WaitReady(AnalysisPageBase page)
        {
            var until = DateTime.UtcNow.AddMinutes(4);
            while (page.State is RunState.Running or RunState.Idle && DateTime.UtcNow < until) await Task.Delay(300);
            await Flush();
        }

        void Shoot(string page, string? extra = null)
        {
            foreach (var theme in new[] { UiPrefs.Night, UiPrefs.Sky })
            {
                ThemeManager.Apply(theme, save: false);
                win.UpdateLayout();
                Save(win, Path.Combine(dir, $"phase4-{page}{extra}-{(theme == UiPrefs.Sky ? "sky" : "night")}.png"));
            }
            ThemeManager.Apply(UiPrefs.Night, save: false);
        }

        // empty pages first
        vm.Select("Binary"); await Flush(); Shoot("binary", "-empty");
        vm.Select("Js"); await Flush(); Shoot("js", "-empty");

        if (files.Length > 0)
        {
            await vm.OpenEntryAsync(files[0], FileKindDetector.Detect(files[0]).Kind);
            await WaitReady(vm.Binary);
            await Flush(); Shoot("binary-dotnet");
            vm.Binary.Tab = BinTab.Meta; await Flush(); Shoot("binary-dotnet", "-meta");
            vm.Binary.Tab = BinTab.Cil;
        }
        if (files.Length > 1)
        {
            await vm.OpenEntryAsync(files[1], FileKindDetector.Detect(files[1]).Kind);
            await WaitReady(vm.Js);
            await Flush(); Shoot("js");
        }
        if (files.Length > 2)
        {
            await vm.OpenEntryAsync(files[2], FileKindDetector.Detect(files[2]).Kind);
            await WaitReady(vm.Binary);
            await Flush(); Shoot("binary-native");
        }
        win.Close();
        try { Directory.Delete(tmpRoot, true); } catch (IOException) { }
    }

    /// <summary>Phase 5: AI page with the mock's sample conversation (no API call, temp key store) + no-key state + settings.</summary>
    public static async Task RunPhase5Async(string dir)
    {
        Directory.CreateDirectory(dir);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "rx-shot5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var ai = new AiServices
        {
            Keys = new ReverseX.Core.Ai.ApiKeyStore(Path.Combine(tmpRoot, "apikey.bin")),
            Prefs = new ReverseX.Core.Ai.AiPrefs(),
            PrefsPath = Path.Combine(tmpRoot, "ai.json"),
        };
        var vm = new MainViewModel(new ProjectStore(Path.Combine(tmpRoot, "projects.json")), snapshots: new SnapshotStore(Path.Combine(tmpRoot, "projects")), ai: ai);
        var win = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowInTaskbar = false, Width = 1280, Height = 800,
        };
        win.Show();
        await Flush();

        void Shoot(string name)
        {
            foreach (var theme in new[] { UiPrefs.Night, UiPrefs.Sky })
            {
                ThemeManager.Apply(theme, save: false);
                win.UpdateLayout();
                Save(win, Path.Combine(dir, $"phase5-{name}-{(theme == UiPrefs.Sky ? "sky" : "night")}.png"));
            }
            ThemeManager.Apply(UiPrefs.Night, save: false);
        }

        vm.Select("Ai"); await Flush(); Shoot("ai-nokey");
        vm.Ai.LoadDemo(running: true); await Flush(); Shoot("ai-running");
        vm.Ai.LoadDemo(running: false); await Flush(); Shoot("ai-proposal");
        vm.Select("Settings"); await Flush(); Shoot("settings");
        win.Close();
        try { Directory.Delete(tmpRoot, true); } catch (IOException) { }
    }

    private static async Task Flush()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(400);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static void Save(Window w, string file)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var width = (int)Math.Ceiling(w.ActualWidth);
        var height = (int)Math.Ceiling(w.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
