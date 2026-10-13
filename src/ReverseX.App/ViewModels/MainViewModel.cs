using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Projects;
using ReverseX.Core.Tools;

namespace ReverseX.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, object> _pages = new();
    private object _current;
    private bool _isNight = ThemeManager.Current == UiPrefs.Night;
    private bool _isThai = Loc.Current == UiPrefs.Thai;

    public MainViewModel(ProjectStore store, Func<CancellationToken, Task<IReadOnlyList<ToolStatus>>>? toolCheck = null,
        IAnalysisBackend? backend = null, SnapshotStore? snapshots = null)
    {
        backend ??= new ReaAnalysisBackend();
        snapshots ??= new SnapshotStore();
        Nav = new[]
        {
            new NavItemViewModel("Home", "home.png"),
            new NavItemViewModel("Ai", "ai.png"),
            new NavItemViewModel("Binary", "binary.png"),
            new NavItemViewModel("Js", "javascript.png"),
            new NavItemViewModel("Android", "android.png"),
            new NavItemViewModel("Dotnet", "dotnet.png"),
            new NavItemViewModel("Web", "web.png"),
            new NavItemViewModel("Db", "database.png"),
            new NavItemViewModel("Evidence", "evidence.png"),
            new NavItemViewModel("Settings", "settings.png"),
        };
        Home = new HomeViewModel(store, toolCheck);
        Home.OpenAiRequested += () => Select("Ai");
        _pages["Home"] = Home;
        foreach (var n in Nav.Skip(1)) _pages[n.Key] = new PlaceholderViewModel(n);
        Binary = new BinaryViewModel(backend, snapshots, OpenPathAsync);
        Js = new JsViewModel(backend, snapshots, OpenPathAsync);
        _pages["Binary"] = Binary;
        _pages["Js"] = Js;
        Home.OpenRequested += entry => _ = OpenEntryAsync(entry.Path, entry.Kind);
        _current = Home;
        Nav[0].IsSelected = true;
        foreach (var n in Nav)
        {
            var item = n;
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NavItemViewModel.IsSelected) && item.IsSelected) Show(item.Key); };
        }
        Loc.Changed += () =>
        {
            foreach (var n in Nav) n.Refresh();
            foreach (var p in _pages.Values.OfType<PlaceholderViewModel>()) p.Refresh();
        };
    }

    public NavItemViewModel[] Nav { get; }
    public HomeViewModel Home { get; }
    public BinaryViewModel Binary { get; }
    public JsViewModel Js { get; }

    /// <summary>Opens a path from a page's own picker: detects the kind, records it in recent projects, then shows the matching page.</summary>
    private async Task OpenPathAsync(string path)
    {
        // AddPathsAsync raises Home.OpenRequested for a single supported file, which routes to the page.
        await Home.AddPathsAsync(new[] { path }).ConfigureAwait(true);
    }

    /// <summary>Routes a project to its page and starts (or reloads) the analysis there. Kinds without a page yet stay on Overview.</summary>
    public async Task OpenEntryAsync(string path, FileKind kind)
    {
        switch (kind)
        {
            case FileKind.Pe or FileKind.DotNet or FileKind.Elf or FileKind.MachO:
                Select("Binary");
                await Binary.OpenAsync(path, kind).ConfigureAwait(true);
                break;
            case FileKind.Asar or FileKind.AppFolder or FileKind.Folder:
                Select("Js");
                await Js.OpenAsync(path, kind).ConfigureAwait(true);
                break;
        }
    }
    public object Current { get => _current; private set => Set(ref _current, value); }

    public bool IsNight
    {
        get => _isNight;
        set
        {
            if (!Set(ref _isNight, value)) return;
            Raise(nameof(IsSky));
            if (value) ThemeManager.Apply(UiPrefs.Night);
        }
    }

    public bool IsSky
    {
        get => !_isNight;
        set
        {
            if (value == !_isNight) return;
            _isNight = !value;
            Raise(nameof(IsNight));
            Raise();
            if (value) ThemeManager.Apply(UiPrefs.Sky);
        }
    }

    public bool IsThai
    {
        get => _isThai;
        set
        {
            if (!Set(ref _isThai, value)) return;
            Raise(nameof(IsEnglish));
            if (value) Loc.Apply(UiPrefs.Thai);
        }
    }

    public bool IsEnglish
    {
        get => !_isThai;
        set
        {
            if (value == !_isThai) return;
            _isThai = !value;
            Raise(nameof(IsThai));
            Raise();
            if (value) Loc.Apply(UiPrefs.English);
        }
    }

    public void Select(string key)
    {
        var item = Nav.First(n => n.Key == key);
        item.IsSelected = true;
        Show(key);
    }

    private void Show(string key) => Current = _pages[key];
}
