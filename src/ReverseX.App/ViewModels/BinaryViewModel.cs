using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

public sealed record SymbolItem(string Name, string Sub, string Key, DotNetMethod? Method, NativeProcedure? Proc);
public sealed record MetaRow(string Label, string Value);
public sealed record CilRow(string Offset, string Opcode, string Operand, string Kind);
public sealed record RefRow(string Text, string Kind);
public sealed record EvidenceRow(EvidenceLevel Level, string Badge, string Text);

public enum BinTab { Cil, Meta, Go, Native }

/// <summary>The binary page: .NET assemblies (real today), native files (engine needed), Go build info.</summary>
public sealed class BinaryViewModel : AnalysisPageBase, IEvidenceSource
{
    public const string GuideUrl = "https://github.com/morluto/rea#readme";
    private const int MaxCilRows = 3000;
    private const int MaxListRows = 20000;

    private readonly Func<string, Task>? _open;
    private IReadOnlyList<SymbolItem> _all = Array.Empty<SymbolItem>();
    private IReadOnlyList<SymbolItem> _view = Array.Empty<SymbolItem>();
    private SymbolItem? _selected;
    private string _search = "";
    private BinTab _tab = BinTab.Cil;
    private DotNetModel? _dotnet;
    private NativeModel? _native;
    private GoBuildInfoModel? _go;
    private string _dossier = "";
    private bool _dossierBusy;
    private CancellationTokenSource? _dossierCts;

    public BinaryViewModel(IAnalysisBackend backend, SnapshotStore snapshots, Func<string, Task>? openPath)
        : base(backend, snapshots, openPath)
    {
        _open = openPath;
        PickCommand = new RelayCommand(() =>
        {
            var p = Dialogs.PickFile(Loc.Get("Dlg.PickFile"), "EXE / DLL|*.exe;*.dll;*.sys;*.so;*.dylib|" + Loc.Get("Dlg.AllFiles") + "|*.*");
            if (p is not null && _open is not null) _ = _open(p);
        });
        OpenGuideCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        });
        LoadGraphCommand = new RelayCommand(() => { _ = LoadDossierAsync(graph: true); }, () => _selected?.Proc is not null && !_dossierBusy);
    }

    protected override string TitleKey => "Bin.Title";
    protected override bool Accepts(AnalysisKind saved, FileKind target) =>
        target == FileKind.DotNet ? saved == AnalysisKind.DotNet : saved == AnalysisKind.Native;

    public string EvidenceSourceName => Loc.Get("Nav.Binary");
    public string? EvidenceTarget => IsReady ? TargetPath : null;
    public IReadOnlyList<EvidenceRow> EvidenceItems => Evidence;

    public ICommand PickCommand { get; }
    public ICommand OpenGuideCommand { get; }
    public ICommand LoadGraphCommand { get; }

    // ───────── state shown by the page ─────────

    public bool HasTarget => TargetPath is not null;
    public bool ShowEmpty => IsIdle && !HasTarget;
    public bool IsDotNet => _dotnet is not null;
    public bool IsNative => _native is not null;
    public bool HasGo => _go is not null;
    public bool ShowNoEngine => _native is { EngineMissing: true };
    public bool HasSymbols => _all.Count > 0;
    public bool ShowSymbolPanes => IsReady && (IsDotNet || (IsNative && !ShowNoEngine));
    public bool ShowCenterPlain => IsReady && !ShowSymbolPanes;
    public bool ShowFirstTab => ShowSymbolPanes;
    public string FirstTabLabel => Loc.Get(IsNative ? "Nat.Dossier" : "Bin.Tab.Cil");

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (_dotnet is { } d)
            {
                parts.Add(FrameworkText(d.Info.TargetFramework ?? d.Info.RuntimeFamily ?? ""));
                if (d.Info.Architecture is { Length: > 0 } a) parts.Add(a);
                parts.Add(FormatSize(d.Info.ByteLength));
                if (d.Info.Sha256.Length >= 12) parts.Add("SHA-256 " + d.Info.Sha256[..4] + "…" + d.Info.Sha256[^4..]);
            }
            else if (_native?.Identity is { } n)
            {
                parts.Add(n.Format.ToUpperInvariant() + (n.Architecture is { Length: > 0 } ar ? " · " + ar : ""));
                if (Snapshot is { } s) parts.Add(FormatSize(s.SourceSize));
                if (n.Sha256.Length >= 12) parts.Add("SHA-256 " + n.Sha256[..4] + "…" + n.Sha256[^4..]);
            }
            return string.Join(" · ", parts.Where(p => p.Trim().Length > 0));
        }
    }

    public IReadOnlyList<SymbolItem> Items { get => _view; private set => Set(ref _view, value); }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    public string AllCountText => Loc.Format(IsNative ? "Nat.Count" : "Bin.All", _all.Count.ToString("N0", CultureInfo.CurrentCulture));
    public string TypeCountText => _dotnet is null ? "" : Loc.Format("Bin.Types", _dotnet.Types.Count.ToString("N0", CultureInfo.CurrentCulture));
    public bool HasTypeCount => _dotnet is not null;
    public string ListNote => _all.Count >= MaxListRows ? Loc.Format("Js.Truncated", _all.Count, _native?.ProcedureTotal ?? _all.Count) : "";
    public string SearchHint => Loc.Get(IsNative ? "Nat.Search" : "Bin.Search");

    public SymbolItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            RebuildSelection();
            if (IsNative && value?.Proc is not null) _ = LoadDossierAsync(graph: false);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public BinTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Raise(nameof(TabCil)); Raise(nameof(TabMeta)); Raise(nameof(TabGo)); Raise(nameof(TabNative));
            Raise(nameof(ShowCil)); Raise(nameof(ShowMeta)); Raise(nameof(ShowGo)); Raise(nameof(ShowNativeInfo));
        }
    }

    public bool TabCil { get => _tab == BinTab.Cil; set { if (value) Tab = BinTab.Cil; } }
    public bool TabMeta { get => _tab == BinTab.Meta; set { if (value) Tab = BinTab.Meta; } }
    public bool TabGo { get => _tab == BinTab.Go; set { if (value) Tab = BinTab.Go; } }
    public bool TabNative { get => _tab == BinTab.Native; set { if (value) Tab = BinTab.Native; } }
    public bool ShowCil => _tab == BinTab.Cil;
    public bool ShowMeta => _tab == BinTab.Meta;
    public bool ShowGo => _tab == BinTab.Go;
    public bool ShowNativeInfo => _tab == BinTab.Native;

    // ───────── center ─────────

    public IReadOnlyList<CilRow> CilRows { get; private set; } = Array.Empty<CilRow>();
    public string CilHeader { get; private set; } = "";
    public string CilNote { get; private set; } = "";
    public IReadOnlyList<MetaRow> MetaRows { get; private set; } = Array.Empty<MetaRow>();
    public IReadOnlyList<string> Limitations { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<MetaRow> NativeRows { get; private set; } = Array.Empty<MetaRow>();
    public IReadOnlyList<string> EngineReasons { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<MetaRow> GoRows { get; private set; } = Array.Empty<MetaRow>();
    public IReadOnlyList<MetaRow> GoDeps { get; private set; } = Array.Empty<MetaRow>();
    public IReadOnlyList<MetaRow> GoSettings { get; private set; } = Array.Empty<MetaRow>();
    public string GoDepsTitle { get; private set; } = "";
    public string DossierText { get => _dossier; private set => Set(ref _dossier, value); }
    public bool DossierBusy { get => _dossierBusy; private set => Set(ref _dossierBusy, value); }

    // ───────── right ─────────

    public IReadOnlyList<RefRow> Refs { get; private set; } = Array.Empty<RefRow>();
    public bool HasRefs => Refs.Count > 0;
    public IReadOnlyList<EvidenceRow> Evidence { get; private set; } = Array.Empty<EvidenceRow>();
    public string SelectedTitle => _selected is null ? "" : _selected.Name;
    public string SelectedSub => _selected?.Sub ?? "";

    // ───────── apply ─────────

    protected override void ClearResult()
    {
        _dotnet = null; _native = null; _go = null;
        _all = Array.Empty<SymbolItem>();
        _selected = null;
        _dossierCts?.Cancel();
        DossierText = "";
        RaiseAll();
    }

    protected override void ApplySnapshot(AnalysisSnapshot snap)
    {
        _dotnet = snap.DotNet;
        _native = snap.Native;
        _go = snap.Go;
        _selected = null;
        DossierText = "";

        if (_dotnet is not null)
        {
            _all = _dotnet.Types
                .SelectMany(t => t.Methods.Select(m => new SymbolItem(m.Name, t.FullName, m.Token, m, null)))
                .ToList();
            _tab = BinTab.Cil;
        }
        else if (_native is not null)
        {
            _all = _native.Procedures.Take(MaxListRows).Select(p => new SymbolItem(p.Name, p.Address, p.Address, null, p)).ToList();
            _tab = ShowNoEngine || _all.Count == 0 ? BinTab.Native : BinTab.Cil;
        }
        _search = "";
        _view = Filter(_all, "");
        BuildStatic();
        // first method that actually has IL, so the page opens on something readable
        _selected = _dotnet is null ? null : _view.FirstOrDefault(i => i.Method is { HasBody: true, Anchors.Count: > 0 });
        RebuildSelection();
        RaiseAll();
    }

    protected override void OnStateChanged()
    {
        Raise(nameof(ShowEmpty)); Raise(nameof(ShowSymbolPanes)); Raise(nameof(ShowCenterPlain));
    }

    protected override void OnLanguageChanged()
    {
        if (Snapshot is null) return;
        BuildStatic();
        RebuildSelection();
        RaiseAll();
    }

    // ───────── builders ─────────

    /// <summary>".NETCoreApp,Version=v8.0" → ".NET 8.0"; ".NETFramework,Version=v4.8" → ".NET Framework 4.8"; anything else as-is.</summary>
    public static string FrameworkText(string tfm)
    {
        var i = tfm.IndexOf(",Version=v", StringComparison.Ordinal);
        if (i < 0) return tfm;
        var name = tfm[..i];
        var ver = tfm[(i + 10)..];
        return name switch
        {
            ".NETCoreApp" => ".NET " + ver,
            ".NETFramework" => ".NET Framework " + ver,
            ".NETStandard" => ".NET Standard " + ver,
            _ => tfm,
        };
    }

    private static string FormatSize(long b) =>
        b >= 1 << 20 ? (b / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : b >= 1 << 10 ? (b / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : b + " B";

    private void BuildStatic()
    {
        var meta = new List<MetaRow>();
        var limits = new List<string>();
        if (_dotnet is { } d)
        {
            var i = d.Info;
            meta.Add(new(Loc.Get("Meta.Name"), i.Name));
            meta.Add(new(Loc.Get("Meta.Version"), i.Version));
            meta.Add(new(Loc.Get("Meta.Framework"), (i.TargetFramework is { } tf ? FrameworkText(tf) : "—") + (i.RuntimeFamily is { Length: > 0 } rf ? " · " + rf : "")));
            meta.Add(new(Loc.Get("Meta.Arch"), i.Architecture ?? "—"));
            meta.Add(new(Loc.Get("Meta.Size"), FormatSize(i.ByteLength)));
            meta.Add(new(Loc.Get("Meta.Sha"), i.Sha256));
            meta.Add(new(Loc.Get("Meta.Mvid"), i.Mvid ?? "—"));
            meta.Add(new(Loc.Get("Meta.Metadata"), i.MetadataVersion ?? "—"));
            meta.Add(new(Loc.Get("Meta.Coverage"), i.CoverageState ?? "—"));
            meta.Add(new(Loc.Get("Meta.Refs"), i.References.Count == 0 ? "—" : string.Join(", ", i.References.Take(12).Select(r => r.Name)) + (i.References.Count > 12 ? $" +{i.References.Count - 12}" : "")));
            meta.Add(new(Loc.Get("Meta.Resources"), i.ResourceCount.ToString(CultureInfo.CurrentCulture)));
            meta.Add(new(Loc.Get("Meta.Attrs"), i.AttributeCount.ToString(CultureInfo.CurrentCulture)));
            limits.AddRange(i.Limitations);
            limits.AddRange(d.MembersLimitations.Where(l => !limits.Contains(l)));
        }
        MetaRows = meta;
        Limitations = limits;

        var nat = new List<MetaRow>();
        var reasons = new List<string>();
        if (_native is { } n)
        {
            if (n.Identity is { } id)
            {
                nat.Add(new(Loc.Get("Meta.Format"), id.Format));
                nat.Add(new(Loc.Get("Meta.Kind"), id.Kind));
                nat.Add(new(Loc.Get("Meta.Arch"), id.Architecture ?? "—"));
                nat.Add(new(Loc.Get("Meta.Size"), FormatSize(Snapshot?.SourceSize ?? 0)));
                nat.Add(new(Loc.Get("Meta.Sha"), id.Sha256));
            }
            reasons.AddRange(n.Rejections.Select(r => Loc.Format("Nat.Reason", r.ProviderId, r.Reason)));
        }
        NativeRows = nat;
        EngineReasons = reasons;

        var go = new List<MetaRow>();
        var deps = new List<MetaRow>();
        var sets = new List<MetaRow>();
        if (_go is { } g)
        {
            go.Add(new(Loc.Get("Go.Version"), g.Version));
            if (g.Path is { Length: > 0 }) go.Add(new("path", g.Path));
            if (g.MainModule is { Length: > 0 }) go.Add(new(Loc.Get("Go.Main"), g.MainModule + (g.MainVersion is { Length: > 0 } mv ? " " + mv : "")));
            deps.AddRange(g.Dependencies.Select(x => new MetaRow(x.Path, x.Version)));
            sets.AddRange(g.Settings.Select(x => new MetaRow(x.Key, x.Value)));
            GoDepsTitle = Loc.Format("Go.Deps", g.Dependencies.Count);
        }
        GoRows = go; GoDeps = deps; GoSettings = sets;
    }

    private void RebuildSelection()
    {
        var cil = new List<CilRow>();
        var refs = new List<RefRow>();
        var ev = new List<EvidenceRow>();
        CilHeader = "";
        CilNote = Loc.Get("Bin.CilNote");

        var conf = EvidenceLevels.FromRea(_dotnet?.Info.Confidence);
        EvidenceRow Row(EvidenceLevel lv, string text) =>
            new(lv, Loc.Get(lv switch { EvidenceLevel.Seen => "Ev.Seen", EvidenceLevel.Inferred => "Ev.Inferred", _ => "Ev.Unknown" }), text);

        if (_dotnet is { } d)
        {
            if (_selected?.Method is { } m)
            {
                CilHeader = m.Signature;
                if (!m.HasBody) CilNote = Loc.Get("Bin.NoBody");
                foreach (var a in m.Anchors.Take(MaxCilRows))
                    cil.Add(new CilRow("IL_" + a.IlOffset.ToString("X4", CultureInfo.InvariantCulture), a.Opcode, a.Operand, a.OperandKind));
                foreach (var c in m.Calls) refs.Add(new RefRow(c.Target, "call"));
                foreach (var f in m.FieldAccesses) refs.Add(new RefRow(f, "field"));

                if (m.HasBody)
                {
                    ev.Add(Row(conf, Loc.Format("Bin.Sum.Method", m.DeclaringType.Split('.').Last() + "." + m.Name, m.IlSize ?? 0, m.InstructionCount ?? 0)));
                    ev.Add(Row(conf, Loc.Format("Bin.Sum.Calls", m.Calls.Count, m.FieldAccesses.Count)));
                }
                else
                {
                    ev.Add(Row(EvidenceLevel.Unknown, Loc.Get("Bin.Sum.NoBody")));
                }
            }
            else
            {
                ev.Add(Row(conf, Loc.Format("Bin.Sum.Asm", d.Info.Name, d.Types.Count, d.MethodCount, d.Info.References.Count)));
            }
            if (d.Info.CoverageState is { Length: > 0 } cs && cs != "complete")
                ev.Add(Row(EvidenceLevel.Unknown, Loc.Format("Bin.Sum.Partial", cs)));
            if (conf == EvidenceLevel.Inferred)
                ev.Add(Row(EvidenceLevel.Inferred, Loc.Format("Bin.Sum.Derived", d.Info.Confidence)));
            foreach (var l in Limitations.Take(2)) ev.Add(Row(EvidenceLevel.Unknown, l));
        }

        CilRows = cil;
        Refs = refs;
        Evidence = ev;
        Raise(nameof(CilRows)); Raise(nameof(CilHeader)); Raise(nameof(CilNote));
        Raise(nameof(Refs)); Raise(nameof(HasRefs)); Raise(nameof(Evidence));
        Raise(nameof(SelectedTitle)); Raise(nameof(SelectedSub));
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(HasTarget), nameof(ShowEmpty), nameof(IsDotNet), nameof(IsNative), nameof(HasGo), nameof(ShowNoEngine),
            nameof(HasSymbols), nameof(ShowSymbolPanes), nameof(ShowCenterPlain), nameof(ShowFirstTab), nameof(FirstTabLabel), nameof(Subtitle), nameof(Items), nameof(Search),
            nameof(AllCountText), nameof(TypeCountText), nameof(HasTypeCount), nameof(ListNote), nameof(SearchHint), nameof(Selected),
            nameof(Tab), nameof(TabCil), nameof(TabMeta), nameof(TabGo), nameof(TabNative), nameof(ShowCil), nameof(ShowMeta),
            nameof(ShowGo), nameof(ShowNativeInfo), nameof(MetaRows), nameof(Limitations), nameof(NativeRows), nameof(EngineReasons),
            nameof(GoRows), nameof(GoDeps), nameof(GoSettings), nameof(GoDepsTitle), nameof(TargetName),
        }) Raise(n);
    }

    // ───────── filtering ─────────

    private static IReadOnlyList<SymbolItem> Filter(IReadOnlyList<SymbolItem> all, string q)
    {
        q = q.Trim();
        if (q.Length == 0) return all;
        return all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || i.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || i.Key.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ApplyFilter() => Items = Filter(_all, _search);

    // ───────── native: dossier / call graph ─────────

    private async Task LoadDossierAsync(bool graph)
    {
        var item = _selected;
        var path = TargetPath;
        if (item?.Proc is null || path is null) return;
        _dossierCts?.Cancel();
        var cts = _dossierCts = new CancellationTokenSource();
        DossierBusy = true;
        DossierText = "…";
        try
        {
            var text = await Task.Run(() => graph
                ? Backend.CallGraphAsync(path, item.Proc.Address, cts.Token)
                : Backend.FunctionDossierAsync(path, item.Proc.Address, cts.Token)).ConfigureAwait(true);
            if (!ReferenceEquals(_dossierCts, cts)) return;
            DossierText = Pretty(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppPaths_Log(ex);
            if (ReferenceEquals(_dossierCts, cts)) DossierText = Loc.Get(ex is AnalysisFailure f && f.Kind == AnalysisFailureKind.NotSupported ? "Err.NotSupported" : "Err.Analysis");
        }
        finally
        {
            if (ReferenceEquals(_dossierCts, cts)) DossierBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void AppPaths_Log(Exception ex) => ReverseX.Core.AppPaths.Log("Function dossier failed", ex);

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return s.Length > 200_000 ? s[..200_000] + "\n…" : s;
        }
        catch (JsonException) { return json.Length > 200_000 ? json[..200_000] : json; }
    }
}
