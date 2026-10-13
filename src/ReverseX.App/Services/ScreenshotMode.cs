using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReverseX.App.ViewModels;
using ReverseX.Core;
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
