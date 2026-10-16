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
using ReverseX.Licensing;

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
    /// <summary>--phase8 &lt;files…&gt;: opens each file (APK / SQLite / HAR / WASM / .NET) and shoots the Insight pages and the Evidence page.</summary>
    public static async Task RunPhase8Async(string dir, string[] files)
    {
        Directory.CreateDirectory(dir);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "rx-shot8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var vm = new MainViewModel(new ProjectStore(Path.Combine(tmpRoot, "projects.json")), snapshots: new SnapshotStore(Path.Combine(tmpRoot, "projects")));
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
                Save(win, Path.Combine(dir, $"phase8-{name}-{(theme == UiPrefs.Sky ? "sky" : "night")}.png"));
            }
            ThemeManager.Apply(UiPrefs.Night, save: false);
        }

        vm.Select("Android"); await Flush(); Shoot("android-empty");
        vm.Select("Evidence"); await Flush(); Shoot("evidence-empty");

        foreach (var f in files)
        {
            await vm.OpenEntryAsync(f, FileKindDetector.Detect(f).Kind);
            var pages = new[] { ("android", vm.Android), ("web", vm.Web), ("db", vm.Db), ("dotnet", vm.Dotnet) };
            var until = DateTime.UtcNow.AddMinutes(4);
            while (pages.Any(p => p.Item2.State == RunState.Running) && DateTime.UtcNow < until) await Task.Delay(300);
            await Flush();
            foreach (var (name, page) in pages.Where(p => p.Item2.TargetPath == f))
            {
                vm.Select(page.Section.Key); await Flush();
                Shoot(name + "-" + Path.GetExtension(f).TrimStart('.'));
            }
        }
        vm.Select("Evidence"); await Flush(); Shoot("evidence");
        win.Close();
        try { Directory.Delete(tmpRoot, true); } catch (IOException) { }
    }

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

    /// <summary>"--screenshots DIR --phase6": the license + update cards, the register gate and the Pro lock, with fixed states (no server).</summary>
    public static async Task RunPhase6Async(string dir)
    {
        Directory.CreateDirectory(dir);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "rx-shot6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var ai = new AiServices
        {
            Keys = new ReverseX.Core.Ai.ApiKeyStore(Path.Combine(tmpRoot, "apikey.bin")),
            Prefs = new ReverseX.Core.Ai.AiPrefs(),
            PrefsPath = Path.Combine(tmpRoot, "ai.json"),
        };
        var now = DateTime.UtcNow;
        var lic = new FixedLicenseSource(new LicenseStatus
        {
            Tier = LicenseTier.Trial, Reason = LicenseReason.TrialActive, DaysLeft = 9, KeyMasked = "DEMO-…-7Q2K", LicenseType = "demo",
            ExpiresAt = now.AddDays(9), LastVerifiedAt = now.AddHours(-2),
        });
        var upd = new FixedUpdateSource(new UpdateStatus { Current = "0.2.0", Latest = "0.2.0", CheckedAt = now.AddMinutes(-40) });
        var license = new LicenseViewModel(lic, upd);
        var vm = new MainViewModel(new ProjectStore(Path.Combine(tmpRoot, "projects.json")), snapshots: new SnapshotStore(Path.Combine(tmpRoot, "projects")), ai: ai, license: license);
        var win = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowInTaskbar = false, Width = 1280, Height = 800,
        };
        win.Show();
        await Flush();

        async Task Shoot(string name)
        {
            foreach (var theme in new[] { UiPrefs.Night, UiPrefs.Sky })
            {
                ThemeManager.Apply(theme, save: false);
                vm.SyncToggles();
                await Flush();
                Save(win, Path.Combine(dir, $"phase6-license-{name}-{(theme == UiPrefs.Sky ? "sky" : "night")}.png"));
            }
            ThemeManager.Apply(UiPrefs.Night, save: false);
        }

        vm.Select("Settings"); await Flush();
        await Shoot("trial");

        lic.Set(new LicenseStatus { Tier = LicenseTier.Free, Reason = LicenseReason.TrialEnded, LastVerifiedAt = now.AddDays(-1), Note = LicenseNote.Offline });
        upd.Set(new UpdateStatus { Current = "0.2.0", Latest = "0.3.0", Available = true, State = UpdateState.Failed, Error = UpdateError.BadSignature, CheckedAt = now.AddMinutes(-3) });
        license.ShowUpdateError(UpdateError.BadSignature);
        license.ShowNotice(Loc.Get("Lic.Act.OtherDevice"), error: true);
        await Flush();
        await Shoot("free");

        Loc.Apply(UiPrefs.English, save: false);
        vm.SyncToggles();
        lic.Set(new LicenseStatus
        {
            Tier = LicenseTier.Pro, Reason = LicenseReason.ProUntil, DaysLeft = 287, KeyMasked = "RVX7-…-K9M2", LicenseType = "yearly",
            ExpiresAt = now.AddDays(287), LastVerifiedAt = now.AddHours(-1), HasPaidKey = true,
        });
        upd.Set(new UpdateStatus { Current = "0.2.0", Latest = "0.3.0", Available = true, State = UpdateState.Downloading, Progress = 0.62, CheckedAt = now.AddMinutes(-1) });
        license.ShowUpdateNotice("", false);
        license.ShowNotice(Loc.Get("Lic.Act.Activated"), error: false);
        await Flush();
        await Shoot("pro-en");

        upd.Set(new UpdateStatus { Current = "0.2.0", Latest = "0.3.0", Available = true, State = UpdateState.Failed, Error = UpdateError.Incomplete, CheckedAt = now.AddMinutes(-1) });
        license.ShowUpdateError(UpdateError.Incomplete);
        await Flush();
        await Shoot("incomplete-en");
        Loc.Apply(UiPrefs.Thai, save: false);
        vm.SyncToggles();

        lic.Set(new LicenseStatus { Tier = LicenseTier.Free, Reason = LicenseReason.FreeNoKey, LastVerifiedAt = now });
        vm.Select("Ai"); await Flush();
        await Shoot("ai-locked");

        lic.Set(new LicenseStatus { Tier = LicenseTier.Unregistered, Reason = LicenseReason.NotRegistered, Note = LicenseNote.Offline });
        license.ShowNotice("", false);
        vm.Select("Home"); await Flush();
        await Shoot("gate");

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
