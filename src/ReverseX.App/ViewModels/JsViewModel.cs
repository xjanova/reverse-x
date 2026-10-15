using System.Globalization;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

public sealed record StatCard(string Title, string Value, string Sub);
public sealed record FlowNode(string Glyph, string Title, string Sub, bool IsFirst, bool IsLast);
public sealed record ModuleRow(string Path, string Name, string Dir, string Kind, bool IsAsset);

/// <summary>The JS / Electron page: summary cards, renderer → preload → IPC → main flow, paged module list.</summary>
public sealed class JsViewModel : AnalysisPageBase, IEvidenceSource
{
    public const int PageSize = 40;

    private readonly Func<string, Task>? _open;
    private JsAppModel? _model;
    private IReadOnlyList<ModuleRow> _all = Array.Empty<ModuleRow>();
    private IReadOnlyList<ModuleRow> _filtered = Array.Empty<ModuleRow>();
    private IReadOnlyList<ModuleRow> _page = Array.Empty<ModuleRow>();
    private int _pageIndex;
    private string _search = "";

    public JsViewModel(IAnalysisBackend backend, SnapshotStore snapshots, Func<string, Task>? openPath)
        : base(backend, snapshots, openPath)
    {
        _open = openPath;
        PickFolderCommand = new RelayCommand(() =>
        {
            var p = Dialogs.PickFolder(Loc.Get("Dlg.PickFolder"));
            if (p is not null && _open is not null) _ = _open(p);
        });
        PickAsarCommand = new RelayCommand(() =>
        {
            var p = Dialogs.PickFile(Loc.Get("Btn.PickAsar"), "ASAR|*.asar|" + Loc.Get("Dlg.AllFiles") + "|*.*");
            if (p is not null && _open is not null) _ = _open(p);
        });
        FirstCommand = new RelayCommand(() => PageIndex = 0, () => PageIndex > 0);
        PrevCommand = new RelayCommand(() => PageIndex--, () => PageIndex > 0);
        NextCommand = new RelayCommand(() => PageIndex++, () => PageIndex < PageCount - 1);
        LastCommand = new RelayCommand(() => PageIndex = PageCount - 1, () => PageIndex < PageCount - 1);
    }

    protected override string TitleKey => "Js.Title";
    protected override bool Accepts(AnalysisKind saved, FileKind target) => saved == AnalysisKind.Js;

    public string EvidenceSourceName => Loc.Get("Nav.Js");
    public string? EvidenceTarget => IsReady ? TargetPath : null;
    public IReadOnlyList<EvidenceRow> EvidenceItems => Evidence;

    public ICommand PickFolderCommand { get; }
    public ICommand PickAsarCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand LastCommand { get; }

    public bool HasTarget => TargetPath is not null;
    public bool ShowEmpty => IsIdle && !HasTarget;

    public JsAppSummary? Summary => _model?.Summary;
    public bool IntegrityWarning => _model?.Summary.IntegrityMismatch == true;
    public string Subtitle =>
        _model is null ? "" : string.Join(" · ", new[] { _model.Summary.Format.ToUpperInvariant(), _model.Summary.Sha256.Length >= 8 ? "SHA-256 " + _model.Summary.Sha256[..4] + "…" + _model.Summary.Sha256[^4..] : "" }.Where(s => s.Length > 0));

    public IReadOnlyList<StatCard> Stats { get; private set; } = Array.Empty<StatCard>();
    public IReadOnlyList<FlowNode> Flow { get; private set; } = Array.Empty<FlowNode>();
    public string FlowNote { get; private set; } = "";
    public IReadOnlyList<EvidenceRow> Evidence { get; private set; } = Array.Empty<EvidenceRow>();

    public IReadOnlyList<ModuleRow> PageItems { get => _page; private set => Set(ref _page, value); }
    public int FilteredCount => _filtered.Count;
    public int PageCount => Paging.PageCount(_filtered.Count, PageSize);
    public string ModuleTitle => Loc.Get("Js.Modules") + (_model is null ? "" : "  ·  " + _model.ModulesTotal.ToString("N0", CultureInfo.CurrentCulture));
    public string PageText => Loc.Format("Js.Page", PageIndex + 1, PageCount);
    public string ShowingText
    {
        get
        {
            if (_filtered.Count == 0) return "";
            var from = PageIndex * PageSize + 1;
            var to = Math.Min(_filtered.Count, from + PageSize - 1);
            return Loc.Format("Js.Showing", from, to, _filtered.Count.ToString("N0", CultureInfo.CurrentCulture));
        }
    }

    public string TruncatedNote =>
        _model is { ModulesComplete: false } m ? Loc.Format("Js.Truncated", m.Modules.Count.ToString("N0", CultureInfo.CurrentCulture), m.ModulesTotal.ToString("N0", CultureInfo.CurrentCulture)) : "";

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            var v = Paging.Clamp(value, _filtered.Count, PageSize);
            if (!Set(ref _pageIndex, v)) return;
            ApplyPage();
        }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    // ───────── apply ─────────

    protected override void ClearResult()
    {
        _model = null;
        _all = _filtered = _page = Array.Empty<ModuleRow>();
        _pageIndex = 0;
        RaiseAll();
    }

    protected override void ApplySnapshot(AnalysisSnapshot snap)
    {
        _model = snap.Js;
        _all = (_model?.Modules ?? new()).Select(ToRow).ToList();
        _search = "";
        _filtered = _all;
        _pageIndex = 0;
        BuildDerived();
        ApplyPage();
        RaiseAll();
    }

    protected override void OnStateChanged() => Raise(nameof(ShowEmpty));

    protected override void OnLanguageChanged()
    {
        if (_model is null) return;
        BuildDerived();
        RaiseAll();
    }

    private static ModuleRow ToRow(JsModuleItem m)
    {
        var p = m.Path.Replace('\\', '/');
        var i = p.LastIndexOf('/');
        var kind = m.Kind.StartsWith("javascript-", StringComparison.Ordinal) ? m.Kind["javascript-".Length..] : m.Kind;
        return new ModuleRow(p, i >= 0 ? p[(i + 1)..] : p, i >= 0 ? p[..i] : "", kind, kind == "asset");
    }

    private void BuildDerived()
    {
        if (_model is null) return;
        var s = _model.Summary;
        string N(int v) => v.ToString("N0", CultureInfo.CurrentCulture);

        var kinds = string.Join(" · ", _all.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} {N(g.Count())}"));
        Stats = new[]
        {
            new StatCard(Loc.Get("Js.Stat.Modules"), N(_model.ModulesTotal), kinds),
            new StatCard(Loc.Get("Js.Stat.Ipc"), N(s.IpcOperations), Loc.Format("Js.Stat.IpcSub", s.IpcLiteralChannels, s.IpcDynamicOperations)),
            new StatCard(Loc.Get("Js.Stat.Preload"), N(s.PreloadEntrypoints), Loc.Format("Js.Stat.PreloadSub", s.BrowserWindows, s.ContextBridgeApis)),
            new StatCard(Loc.Get("Js.Stat.Addon"), N(s.NativeAddonBindings), Loc.Format("Js.Stat.AddonSub", s.ResolvedNativeAddons)),
            new StatCard(Loc.Get("Js.Stat.Files"), N(s.RelevantFiles), Loc.Format("Js.Stat.FilesSub", s.ParsedFiles, s.ParseFailures)),
        };

        Flow = new[]
        {
            new FlowNode("\uE7F4", Loc.Get("Js.Flow.Renderer"), Loc.Format("Js.Flow.RendererSub", s.RendererTransmissions, s.RendererListeners), true, false),
            new FlowNode("\uE71B", Loc.Get("Js.Flow.Preload"), Loc.Format("Js.Flow.PreloadSub", s.PreloadEntrypoints, s.ContextBridgeApis), false, false),
            new FlowNode("\uE8AB", Loc.Get("Js.Flow.Ipc"), Loc.Format("Js.Flow.IpcSub", s.IpcLiteralChannels), false, false),
            new FlowNode("\uE713", Loc.Get("Js.Flow.Main"), Loc.Format("Js.Flow.MainSub", s.MainHandlers), false, true),
        };
        FlowNote = s.IpcDynamicOperations > 0 ? Loc.Format("Js.Flow.Note", s.IpcDynamicOperations) : "";

        var conf = EvidenceLevels.FromRea(s.Confidence);
        EvidenceRow Row(EvidenceLevel lv, string text) =>
            new(lv, Loc.Get(lv switch { EvidenceLevel.Seen => "Ev.Seen", EvidenceLevel.Inferred => "Ev.Inferred", _ => "Ev.Unknown" }), text);
        var ev = new List<EvidenceRow>
        {
            Row(EvidenceLevel.Seen, Loc.Format("Js.Ev.Files", s.ParsedFiles, s.Findings)),
            Row(conf == EvidenceLevel.Seen ? EvidenceLevel.Seen : EvidenceLevel.Inferred, Loc.Format("Js.Ev.Ipc", s.IpcOperations, s.PairedTransmissions)),
        };
        if (s.IpcDynamicOperations > 0) ev.Add(Row(EvidenceLevel.Unknown, Loc.Format("Js.Ev.Dynamic", s.IpcDynamicOperations)));
        if (s.SemanticCoverage is { Length: > 0 } sc && sc != "complete")
            ev.Add(Row(EvidenceLevel.Unknown, Loc.Format("Js.Ev.Semantic", s.SemanticUnknowns.ToString("N0", CultureInfo.CurrentCulture))));
        if (conf == EvidenceLevel.Inferred) ev.Add(Row(EvidenceLevel.Inferred, Loc.Get("Js.Ev.Derived")));
        foreach (var l in s.Limitations.Where(l => !l.Contains("integrity metadata", StringComparison.OrdinalIgnoreCase)).Take(3))
            ev.Add(Row(EvidenceLevel.Unknown, l));
        Evidence = ev;
    }

    // ───────── filter / page ─────────

    private void ApplyFilter()
    {
        var q = _search.Trim();
        _filtered = q.Length == 0 ? _all : _all.Where(r => r.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _pageIndex = 0;
        ApplyPage();
        Raise(nameof(PageIndex));
    }

    private void ApplyPage()
    {
        PageItems = Paging.Slice(_filtered, _pageIndex, PageSize);
        Raise(nameof(FilteredCount)); Raise(nameof(PageCount)); Raise(nameof(PageText)); Raise(nameof(ShowingText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(HasTarget), nameof(ShowEmpty), nameof(Summary), nameof(IntegrityWarning), nameof(Subtitle), nameof(Stats), nameof(Flow),
            nameof(FlowNote), nameof(Evidence), nameof(PageItems), nameof(FilteredCount), nameof(PageCount), nameof(ModuleTitle),
            nameof(PageText), nameof(ShowingText), nameof(TruncatedNote), nameof(PageIndex), nameof(Search), nameof(TargetName),
        }) Raise(n);
    }
}
