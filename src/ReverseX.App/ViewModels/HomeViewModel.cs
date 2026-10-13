using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Files;
using ReverseX.Core.Projects;
using ReverseX.Core.Tools;

namespace ReverseX.App.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly ProjectStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ToolStatus>>> _toolCheck;
    private List<ProjectItemViewModel> _all = new();
    private string _search = "";
    private bool _dragging;
    private string? _noticeKey;
    private object[] _noticeArgs = Array.Empty<object>();
    private bool _noticeIsError;
    private bool _checkingTools;
    private CancellationTokenSource? _toolCts;

    public HomeViewModel(ProjectStore store, Func<CancellationToken, Task<IReadOnlyList<ToolStatus>>>? toolCheck = null)
    {
        _store = store;
        _toolCheck = toolCheck ?? ToolCheckService.CheckAsync;
        foreach (var t in ToolStatusMapper.Checking()) Tools.Add(new ToolItemViewModel(t));
        RecheckCommand = new RelayCommand(_ => { _ = CheckToolsAsync(); }, _ => !_checkingTools);
        RemoveCommand = new RelayCommand(o => { if (o is ProjectItemViewModel p) Remove(p); });
        DismissNoticeCommand = new RelayCommand(() => SetNotice(null));
        OpenAiCommand = new RelayCommand(() => OpenAiRequested?.Invoke());
        OpenProjectCommand = new RelayCommand(o => { if (o is ProjectItemViewModel p) OpenRequested?.Invoke(p.Entry); });
        Loc.Changed += RefreshText;
        ReloadProjects();
    }

    public event Action? OpenAiRequested;
    /// <summary>Raised when the user opens a project (row click) or adds exactly one supported file.</summary>
    public event Action<ProjectEntry>? OpenRequested;

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = new();
    public ObservableCollection<ToolItemViewModel> Tools { get; } = new();

    public ICommand RecheckCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand OpenAiCommand { get; }
    public ICommand OpenProjectCommand { get; }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }

    public bool ShowEmpty => _all.Count == 0;
    public bool ShowNoMatch => _all.Count > 0 && Projects.Count == 0;
    public int TotalCount => _all.Count;

    // ---- stat cards
    public string ProjectCountText => _all.Count.ToString();
    public string ProjectsSub
    {
        get
        {
            int Count(string g) => _all.Count(p => KindInfo.Group(p.Entry.Kind) == g);
            return Loc.Format("Stat.ProjectsSub", Count("binary"), Count("js"), Count("apk"), Count("other"));
        }
    }

    // ---- notice bar
    public string? Notice => _noticeKey is null ? null : Loc.Format(_noticeKey, _noticeArgs);
    public bool HasNotice => _noticeKey is not null;
    public bool NoticeIsError => _noticeIsError;

    // ---- REA pills / footer, derived from the tools card
    public ToolItemViewModel? Rea => Tools.FirstOrDefault(t => t.Id == "rea");
    public ToolLevel ReaLevel => Rea?.Level ?? ToolLevel.Checking;
    public int ToolProblemCount => Tools.Count(t => t.Level is ToolLevel.Warn or ToolLevel.Bad);
    public string ReaPillText => ReaLevel switch
    {
        ToolLevel.Ok => Loc.Format("Pill.Rea", Rea!.Status.MessageArgs.FirstOrDefault() ?? ReverseX.Core.Rea.ReaLauncher.PinnedVersion),
        ToolLevel.Checking => Loc.Get("Pill.ReaChecking"),
        _ => Loc.Get("Pill.ReaProblem"),
    };
    public string ReaSideText => ReaLevel switch
    {
        ToolLevel.Ok => Loc.Format("Side.ReaReady", Rea!.Status.MessageArgs.FirstOrDefault() ?? ReverseX.Core.Rea.ReaLauncher.PinnedVersion),
        ToolLevel.Checking => Loc.Get("Side.ReaChecking"),
        _ => Loc.Get("Side.ReaProblem"),
    };

    public void ReloadProjects()
    {
        var now = DateTime.UtcNow;
        _all = _store.Load().Select(e => new ProjectItemViewModel(e, now)).ToList();
        ApplyFilter();
        Raise(nameof(ProjectCountText));
        Raise(nameof(ProjectsSub));
        Raise(nameof(TotalCount));
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        var shown = q.Length == 0
            ? _all
            : _all.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || p.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Projects.Clear();
        foreach (var p in shown.Take(8)) Projects.Add(p);
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowNoMatch));
    }

    private void Remove(ProjectItemViewModel p)
    {
        try { _store.Remove(p.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not update projects.json", ex);
            SetNotice("Err.Generic", error: true);
            return;
        }
        ReloadProjects();
    }

    /// <summary>Detects each dropped/picked path and adds it as a project. Never throws; problems become Thai/English notices.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;

        var added = new List<(ProjectEntry Entry, FileKindResult Kind)>();
        string? firstErrorKey = null;
        foreach (var path in list)
        {
            try
            {
                var item = await Task.Run(() =>
                {
                    var k = FileKindDetector.Detect(path);
                    long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                    return (Entry: _store.Add(path, k, size), Kind: k);
                }).ConfigureAwait(true);
                added.Add(item);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                firstErrorKey ??= "Err.NotFound";
            }
            catch (UnauthorizedAccessException)
            {
                firstErrorKey ??= "Err.NoAccess";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                AppPaths.Log("Add project failed: " + path, ex);
                firstErrorKey ??= "Err.Generic";
            }
        }

        ReloadProjects();

        if (firstErrorKey is not null)
        {
            if (added.Count == 0) SetNotice(firstErrorKey, error: true);
            else SetNotice("Notice.Partial", error: true, added.Count, list.Count, Loc.Get(firstErrorKey));
        }
        else if (added.Count == 1)
        {
            var (entry, kind) = added[0];
            if (kind.Kind != FileKind.Unknown) OpenRequested?.Invoke(entry);
            if (kind.Kind == FileKind.Unknown) SetNotice("Notice.AddedUnknown", error: false, entry.Name);
            else SetNotice("Notice.Added", error: false, entry.Name, Loc.Get(KindInfo.NameKey(kind.Kind)));
        }
        else
        {
            SetNotice(null);
        }
    }

    public async Task CheckToolsAsync()
    {
        if (_checkingTools) return;
        _checkingTools = true;
        _toolCts?.Cancel();
        _toolCts = new CancellationTokenSource();
        var ct = _toolCts.Token;
        ApplyTools(ToolStatusMapper.Checking());
        try
        {
            var result = await Task.Run(() => _toolCheck(ct), ct).ConfigureAwait(true);
            ApplyTools(result);
        }
        catch (OperationCanceledException) { /* a newer check replaced this one */ }
        catch (Exception ex)
        {
            AppPaths.Log("Tool check crashed", ex);
            ApplyTools(ToolStatusMapper.Map(new ToolProbeInputs(null, false, null, null, "failed", null, false, null)));
        }
        finally
        {
            _checkingTools = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyTools(IReadOnlyList<ToolStatus> statuses)
    {
        Tools.Clear();
        foreach (var s in statuses) Tools.Add(new ToolItemViewModel(s));
        Raise(nameof(Rea));
        Raise(nameof(ReaLevel));
        Raise(nameof(ReaPillText));
        Raise(nameof(ReaSideText));
        Raise(nameof(ToolProblemCount));
    }

    private void SetNotice(string? key, bool error = false, params object[] args)
    {
        _noticeKey = key;
        _noticeArgs = args;
        _noticeIsError = error;
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(NoticeIsError));
    }

    private void RefreshText()
    {
        foreach (var p in _all) p.Refresh();
        foreach (var t in Tools) t.Refresh();
        Raise(nameof(ProjectsSub));
        Raise(nameof(Notice));
        Raise(nameof(ReaPillText));
        Raise(nameof(ReaSideText));
    }
}
