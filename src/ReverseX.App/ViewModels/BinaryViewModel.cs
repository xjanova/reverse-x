using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Analysis;
using ReverseX.Core.Decompile;
using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

public sealed record SymbolItem(string Name, string Sub, string Key, DotNetMethod? Method, NativeProcedure? Proc);
public sealed record MetaRow(string Label, string Value);
public sealed record CilRow(string Offset, string Opcode, string Operand, string Kind);
/// <param name="Target">The function a native row opens in the list; null for .NET rows, headers, imports and addresses not in the list.</param>
public sealed record RefRow(string Text, string Kind, string Sub = "", SymbolItem? Target = null, bool IsHeader = false)
{
    public bool IsRow => !IsHeader;
    public bool HasSub => Sub.Length > 0;
    public bool CanOpen => Target is not null;
}
public sealed record EvidenceRow(EvidenceLevel Level, string Badge, string Text);

public enum BinTab { Code, Cil, Meta, Go, Native }

/// <summary>The binary page: .NET assemblies decompiled to C#, native files as pseudo-C / assembly (engine needed), Go build info.</summary>
public sealed class BinaryViewModel : AnalysisPageBase, IEvidenceSource
{
    public const string GuideUrl = "https://github.com/morluto/rea#readme";
    private const int MaxCilRows = 3000;
    private const int MaxListRows = 20000;
    private const int MaxRefRows = 500;
    /// <summary>Kind column of native ref rows: a function that calls the selected one, or one it calls.</summary>
    public const string CallerMark = "←", CalleeMark = "→";

    private readonly Func<string, Task>? _open;
    private IReadOnlyList<SymbolItem> _all = Array.Empty<SymbolItem>();
    private IReadOnlyList<SymbolItem> _view = Array.Empty<SymbolItem>();
    private SymbolItem? _selected;
    private string _search = "";
    private BinTab _tab = BinTab.Code;
    private DotNetModel? _dotnet;
    private NativeModel? _native;
    private GoBuildInfoModel? _go;

    // code pane
    private string _codeText = "";
    private string _codeNote = "";
    private bool _codeBusy;
    private bool _wholeType;
    private NativeCodeView _nativeView = NativeCodeView.PseudoCode;
    private int _codeRun;
    private NativeCode? _nativeCode;
    private CancellationTokenSource? _dotnetCts;
    private CancellationTokenSource? _targetCts = new();
    private string? _targetCtsPath;
    private Task<DotNetDecompiler>? _decompiler;
    private string? _decompilerPath;

    // refs pane (native): who calls the selected function and what it calls
    private IReadOnlyDictionary<string, SymbolItem> _byAddress = new Dictionary<string, SymbolItem>();
    // one lookup per function and file; a task so a request still in flight is shared, not sent twice
    private Dictionary<string, Task<NativeRefs>> _refsCache = new(StringComparer.Ordinal);
    private NativeRefs? _nativeRefs;
    private string? _refsFor;
    private string? _refsNoteKey;
    private bool _refsBusy;

    // export
    private bool _exportBusy;
    private string _exportText = "";
    private CancellationTokenSource? _exportCts;

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
        CopyCodeCommand = new RelayCommand(CopyCode, () => _codeText.Length > 0);
        ExportCommand = new RelayCommand(() => { _ = ExportAsync(); }, () => IsDotNet && IsReady && !_exportBusy);
        CancelExportCommand = new RelayCommand(() => _exportCts?.Cancel(), () => _exportBusy);
        // no CanExecute: WPF may ask it before the row's CommandParameter is bound; the row's CanOpen enables the button instead
        OpenRefCommand = new RelayCommand(p => OpenRef(p as RefRow));
    }

    protected override string TitleKey => "Bin.Title";
    protected override bool Accepts(AnalysisKind saved, FileKind target) =>
        target == FileKind.DotNet ? saved == AnalysisKind.DotNet : saved == AnalysisKind.Native;

    public string EvidenceSourceName => Loc.Get("Nav.Binary");
    public string? EvidenceTarget => IsReady ? TargetPath : null;
    public IReadOnlyList<EvidenceRow> EvidenceItems => Evidence;

    public ICommand PickCommand { get; }
    public ICommand OpenGuideCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelExportCommand { get; }
    /// <summary>A native ref row: select that function in the list (and show its code).</summary>
    public ICommand OpenRefCommand { get; }

    // ───────── state shown by the page ─────────

    public bool HasTarget => TargetPath is not null;
    public bool ShowEmpty => IsIdle && !HasTarget;
    public bool IsDotNet => _dotnet is not null;
    public bool IsNative => _native is not null;
    public bool HasGo => _go is not null;
    public bool ShowNoEngine => _native is { EngineMissing: true };
    /// <summary>REA on Windows hands only PE files to Ghidra; ELF and Mach-O need REA on Linux or macOS, so installing engines here would not help.</summary>
    public bool NeedsOtherOs => ShowNoEngine && TargetKind is FileKind.Elf or FileKind.MachO;
    public bool ShowEngineInstall => ShowNoEngine && !NeedsOtherOs && Engines is not null;
    public string NoEngineTitle => Loc.Get(NeedsOtherOs ? "Nat.NoEngine.OtherOsTitle" : "Nat.NoEngine.Title");
    public string NoEngineBody => Loc.Get(NeedsOtherOs ? "Nat.NoEngine.OtherOs" : "Nat.NoEngine.Body");
    /// <summary>Shared install button (also on Home); null in tests and screenshot setups that do not need it.</summary>
    public EngineSetupViewModel? Engines { get; init; }
    public bool HasSymbols => _all.Count > 0;
    public bool ShowSymbolPanes => IsReady && (IsDotNet || (IsNative && !ShowNoEngine));
    public bool ShowCenterPlain => IsReady && !ShowSymbolPanes;
    public string CodeTabLabel => Loc.Get(IsNative ? "Nat.Tab.Code" : "Bin.Tab.CSharp");

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
            _ = LoadCodeAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public BinTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Raise(nameof(TabCode)); Raise(nameof(TabCil)); Raise(nameof(TabMeta)); Raise(nameof(TabGo)); Raise(nameof(TabNative));
            Raise(nameof(ShowCode)); Raise(nameof(ShowCil)); Raise(nameof(ShowMeta)); Raise(nameof(ShowGo)); Raise(nameof(ShowNativeInfo));
            Raise(nameof(ShowSelectionHeader));
        }
    }

    public bool TabCode { get => _tab == BinTab.Code; set { if (value) Tab = BinTab.Code; } }
    public bool TabCil { get => _tab == BinTab.Cil; set { if (value) Tab = BinTab.Cil; } }
    public bool TabMeta { get => _tab == BinTab.Meta; set { if (value) Tab = BinTab.Meta; } }
    public bool TabGo { get => _tab == BinTab.Go; set { if (value) Tab = BinTab.Go; } }
    public bool TabNative { get => _tab == BinTab.Native; set { if (value) Tab = BinTab.Native; } }
    public bool ShowCode => _tab == BinTab.Code;
    public bool ShowCil => _tab == BinTab.Cil;
    public bool ShowMeta => _tab == BinTab.Meta;
    public bool ShowGo => _tab == BinTab.Go;
    public bool ShowNativeInfo => _tab == BinTab.Native;
    public bool ShowSelectionHeader => _tab is BinTab.Code or BinTab.Cil;

    // ───────── center: code ─────────

    public string CodeText { get => _codeText; private set { if (Set(ref _codeText, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string CodeNote { get => _codeNote; private set => Set(ref _codeNote, value); }
    public bool CodeBusy { get => _codeBusy; private set => Set(ref _codeBusy, value); }

    /// <summary>.NET: show the whole declaring class instead of the one method.</summary>
    public bool ScopeType
    {
        get => _wholeType;
        set { if (Set(ref _wholeType, value)) { Raise(nameof(ScopeMethod)); _ = LoadCodeAsync(); } }
    }
    public bool ScopeMethod { get => !_wholeType; set => ScopeType = !value; }

    public bool ViewAsm
    {
        get => _nativeView == NativeCodeView.Assembly;
        set
        {
            var v = value ? NativeCodeView.Assembly : NativeCodeView.PseudoCode;
            if (v == _nativeView) return;
            _nativeView = v;
            Raise(nameof(ViewAsm)); Raise(nameof(ViewPseudo));
            _ = LoadCodeAsync();
        }
    }
    public bool ViewPseudo { get => !ViewAsm; set => ViewAsm = !value; }

    public bool ExportBusy { get => _exportBusy; private set { if (Set(ref _exportBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string ExportText { get => _exportText; private set { if (Set(ref _exportText, value)) Raise(nameof(HasExportText)); } }
    public bool HasExportText => _exportText.Length > 0;

    // ───────── center: CIL / metadata / native / Go ─────────

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

    // ───────── right ─────────

    public IReadOnlyList<RefRow> Refs { get; private set; } = Array.Empty<RefRow>();
    public bool HasRefs => Refs.Count > 0;
    public string RefsTitle => Loc.Get(IsNative ? "Nat.Refs" : "Bin.Refs");
    /// <summary>Native: the engine lookup runs after the code has loaded; only this card shows that it is busy.</summary>
    public bool RefsBusy { get => _refsBusy; private set => Set(ref _refsBusy, value); }
    /// <summary>Why the list is empty: native refs still loading, failed or not asked for; a locally built .NET list has no CIL anchors.</summary>
    public string NoRefsText => Loc.Get(IsNative ? _refsNoteKey ?? "Nat.NoRefs" : _dotnet is { BuiltLocally: true } ? "Bin.NoRefsLocal" : "Bin.NoRefs");
    public IReadOnlyList<EvidenceRow> Evidence { get; private set; } = Array.Empty<EvidenceRow>();
    public string SelectedTitle => _selected is null ? "" : _selected.Name;
    public string SelectedSub => _selected?.Sub ?? "";

    // ───────── apply ─────────

    protected override void ClearResult()
    {
        _dotnet = null; _native = null; _go = null;
        _all = Array.Empty<SymbolItem>();
        _view = Array.Empty<SymbolItem>();
        _byAddress = new Dictionary<string, SymbolItem>();
        _selected = null;
        ResetCode();
        RaiseAll();
    }

    protected override void ApplySnapshot(AnalysisSnapshot snap)
    {
        _dotnet = snap.DotNet;
        _native = snap.Native;
        _go = snap.Go;
        _selected = null;
        ResetCode();

        if (_dotnet is not null)
        {
            // the author's types first; compiler output (regex generators, lambdas, state machines) after, in metadata order
            _all = _dotnet.Types
                .OrderBy(t => IsGenerated(t.FullName))
                .SelectMany(t => t.Methods.Select(m => new SymbolItem(m.Name, t.FullName, m.Token, m, null)))
                .ToList();
            _tab = BinTab.Code;
        }
        else if (_native is not null)
        {
            // the binary's own functions first; imports (no code in this file) at the end
            _all = _native.Procedures
                .OrderBy(p => p.External)
                .Take(MaxListRows)
                .Select(p => new SymbolItem(p.Name, SubFor(p), p.Address, null, p))
                .ToList();
            _byAddress = IndexByAddress(_all);
            _tab = ShowNoEngine || _all.Count == 0 ? BinTab.Native : BinTab.Code;
        }
        _search = "";
        _view = Filter(_all, "");
        BuildStatic();
        _selected = PickFirst();
        RebuildSelection();
        RaiseAll();
        _ = LoadCodeAsync();
    }

    /// <summary>
    /// What the page opens on. .NET: the first method with IL. Native: the entry point — but only right after a live analysis,
    /// when Ghidra's session is warm; from a saved snapshot the first code request costs a full re-analysis, so the user picks.
    /// </summary>
    private SymbolItem? PickFirst()
    {
        if (_dotnet is not null)
            return _view.FirstOrDefault(i => i.Method is { HasBody: true, Anchors.Count: > 0 } && IsPlain(i))
                ?? _view.FirstOrDefault(i => i.Method is { HasBody: true } && IsPlain(i))  // a locally built list has no anchors
                ?? _view.FirstOrDefault(i => i.Method is { HasBody: true });
        if (_native is not null && !FromSnapshot)
            return _view.FirstOrDefault(i => i.Proc is { External: false } p && p.Name == "entry")
                ?? _view.FirstOrDefault(i => i.Proc is { External: false, Thunk: false });
        return null;
    }

    /// <summary>Names only a compiler writes: &lt;PrivateImplementationDetails&gt;, Ns.&lt;RegexGenerator_g&gt;…, Outer+&lt;&gt;c, &lt;Main&gt;b__0_0.</summary>
    internal static bool IsGenerated(string name) => name.Contains('<', StringComparison.Ordinal);

    /// <summary>A method worth opening first: written by the author, not a constructor.</summary>
    private static bool IsPlain(SymbolItem i) => !i.Name.StartsWith('.') && !IsGenerated(i.Name) && !IsGenerated(i.Sub);

    private string SubFor(NativeProcedure p) =>
        p.External ? Loc.Get("Nat.Import") : p.Thunk ? p.Address + " · " + Loc.Get("Nat.Thunk") : p.Address;

    /// <summary>Engine refs are bare entry addresses; the list gives them names and makes them clickable.</summary>
    private static Dictionary<string, SymbolItem> IndexByAddress(IReadOnlyList<SymbolItem> all)
    {
        var map = new Dictionary<string, SymbolItem>(StringComparer.Ordinal);
        foreach (var i in all)
            if (i.Proc is { } p) map.TryAdd(p.Address, i);
        return map;
    }

    protected override void OnStateChanged()
    {
        Raise(nameof(ShowEmpty)); Raise(nameof(ShowSymbolPanes)); Raise(nameof(ShowCenterPlain));
        CommandManager.InvalidateRequerySuggested();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(NoEngineTitle)); Raise(nameof(NoEngineBody)); Raise(nameof(NoRefsText)); Raise(nameof(RefsTitle));
        if (Snapshot is null) return;
        BuildStatic();
        if (_native is not null)
        {
            var sel = _selected?.Key;
            _all = _all.Select(i => i.Proc is { } p ? i with { Sub = SubFor(p) } : i).ToList();
            _byAddress = IndexByAddress(_all);
            _view = Filter(_all, _search);
            _selected = sel is null ? null : _view.FirstOrDefault(i => i.Key == sel);
        }
        RebuildSelection();
        RaiseAll();
        _ = LoadCodeAsync();
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
        CilNote = Loc.Get(_dotnet is { BuiltLocally: true } ? "Bin.CilLocal" : "Bin.CilNote");

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
            if (d.BuiltLocally)
                ev.Add(Row(EvidenceLevel.Unknown, Loc.Get("Bin.Sum.Local")));
            else if (d.Info.CoverageState is { Length: > 0 } cs && cs != "complete")
                ev.Add(Row(EvidenceLevel.Unknown, Loc.Format("Bin.Sum.Partial", cs)));
            if (conf == EvidenceLevel.Inferred)
                ev.Add(Row(EvidenceLevel.Inferred, Loc.Format("Bin.Sum.Derived", d.Info.Confidence)));
            foreach (var l in Limitations.Take(2)) ev.Add(Row(EvidenceLevel.Unknown, l));
        }
        else if (_native is not null && _selected?.Proc is { } p)
        {
            if (_nativeCode is { } code)
            {
                // What the engine itself says about the text on screen: it is the engine's reconstruction, with its own caveats.
                ev.Add(Row(EvidenceLevel.Inferred, Loc.Format(_nativeView == NativeCodeView.Assembly ? "Nat.Sum.Asm" : "Nat.Sum.Pseudo", p.Name, code.Provider)));
                foreach (var l in code.Limitations.Take(3)) ev.Add(Row(EvidenceLevel.Unknown, l));
            }
            if (_nativeRefs is { } r && _refsFor == p.Address)
            {
                AddRefGroup(refs, "Nat.Refs.Callers", r.Callers, CallerMark);
                AddRefGroup(refs, "Nat.Refs.Callees", r.Callees, CalleeMark);
                // references are read straight from the engine's analysis (not reconstructed), but only the ones it could resolve
                ev.Add(Row(EvidenceLevel.Seen, Loc.Format("Nat.Sum.Refs", p.Name, r.Callers.Count, r.Callees.Count, r.Provider)));
                ev.Add(Row(EvidenceLevel.Unknown, Loc.Get("Nat.Sum.RefsIndirect")));
            }
        }

        CilRows = cil;
        Refs = refs;
        Evidence = ev;
        Raise(nameof(CilRows)); Raise(nameof(CilHeader)); Raise(nameof(CilNote));
        Raise(nameof(Refs)); Raise(nameof(HasRefs)); Raise(nameof(Evidence));
        Raise(nameof(SelectedTitle)); Raise(nameof(SelectedSub));
    }

    /// <summary>A header, then one row per address: the list's name when it has one (own functions first, imports last).</summary>
    private void AddRefGroup(List<RefRow> rows, string titleKey, IReadOnlyList<string> addresses, string arrow)
    {
        rows.Add(new RefRow(Loc.Format(titleKey, addresses.Count), "", IsHeader: true));
        if (addresses.Count == 0)
        {
            rows.Add(new RefRow(Loc.Get("Nat.Refs.Empty"), ""));
            return;
        }
        var known = addresses.Select(a => (Address: a, Item: _byAddress.GetValueOrDefault(a))).OrderBy(x => x.Item?.Proc?.External ?? false);
        foreach (var (a, item) in known.Take(MaxRefRows))
        {
            if (item is null) rows.Add(new RefRow(a, arrow, Loc.Get("Nat.Refs.NotListed")));
            // an import has no code in this file: shown, never opened
            else rows.Add(new RefRow(item.Name, arrow, item.Sub, item.Proc is { External: true } ? null : item));
        }
        if (addresses.Count > MaxRefRows) rows.Add(new RefRow(Loc.Format("Nat.Refs.More", addresses.Count - MaxRefRows), ""));
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(HasTarget), nameof(ShowEmpty), nameof(IsDotNet), nameof(IsNative), nameof(HasGo), nameof(ShowNoEngine),
            nameof(NeedsOtherOs), nameof(ShowEngineInstall), nameof(NoEngineTitle), nameof(NoEngineBody),
            nameof(HasSymbols), nameof(ShowSymbolPanes), nameof(ShowCenterPlain), nameof(CodeTabLabel), nameof(Subtitle), nameof(Items), nameof(Search),
            nameof(AllCountText), nameof(TypeCountText), nameof(HasTypeCount), nameof(ListNote), nameof(SearchHint), nameof(Selected),
            nameof(Tab), nameof(TabCode), nameof(TabCil), nameof(TabMeta), nameof(TabGo), nameof(TabNative),
            nameof(ShowCode), nameof(ShowCil), nameof(ShowMeta), nameof(ShowGo), nameof(ShowNativeInfo), nameof(ShowSelectionHeader),
            nameof(MetaRows), nameof(Limitations), nameof(NativeRows), nameof(EngineReasons), nameof(NoRefsText), nameof(RefsTitle), nameof(RefsBusy),
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

    // ───────── code ─────────

    /// <summary>New target or new result: stop work for the old one and forget its decompiler.</summary>
    private void ResetCode()
    {
        _codeRun++;
        // The native token outlives a Reanalyze or a snapshot reload of the same file: cancelling a request that is waiting for
        // Ghidra's whole-file analysis makes REA throw that analysis away. Only a different file cancels it.
        if (_targetCts is null || !string.Equals(_targetCtsPath, TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            _targetCts?.Cancel();
            _targetCts?.Dispose();
            _targetCts = new CancellationTokenSource();
            _targetCtsPath = TargetPath;
        }
        _dotnetCts?.Cancel();
        _dotnetCts?.Dispose();
        _dotnetCts = null;
        _decompiler = null;
        _decompilerPath = null;
        _nativeCode = null;
        CodeText = "";
        CodeNote = "";
        CodeBusy = false;
        _refsCache = new(StringComparer.Ordinal);
        _nativeRefs = null;
        _refsFor = null;
        _refsNoteKey = null;
        RefsBusy = false;
    }

    private Task<DotNetDecompiler> DecompilerFor(string path)
    {
        if (_decompiler is null || !string.Equals(_decompilerPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _decompilerPath = path;
            _decompiler = Task.Run(() => DotNetDecompiler.Open(path));
        }
        return _decompiler;
    }

    private async Task LoadCodeAsync()
    {
        var run = ++_codeRun;
        var item = _selected;
        var path = TargetPath;
        _nativeCode = null;
        // no IsReady check: ApplySnapshot runs this before the base class flips State to Ready
        if (item is null || path is null || (_dotnet is null && _native is null))
        {
            CodeText = "";
            CodeNote = HasSymbols ? Loc.Get(IsNative ? "Nat.Pick" : "Code.Pick") : "";
            CodeBusy = false;
            if (IsNative) ShowRefs(null, null, null);
            return;
        }
        if (item.Proc is { External: true } import)
        {
            CodeText = "";
            CodeNote = Loc.Get("Code.Import");
            CodeBusy = false;
            ShowRefs(import.Address, null, "Nat.Refs.Import");
            return;
        }

        CodeBusy = true;
        CodeText = "";
        CodeNote = Loc.Get(item.Method is not null ? "Code.Loading.Net" : "Code.Loading.Native");
        var native = item.Proc;
        if (native is not null) BeginRefs(native);
        var refsNext = false;
        try
        {
            if (item.Method is { } method)
            {
                // a new click may cancel the previous decompile: it holds nothing worth keeping
                _dotnetCts?.Cancel();
                _dotnetCts?.Dispose();
                var cts = _dotnetCts = CancellationTokenSource.CreateLinkedTokenSource(_targetCts?.Token ?? CancellationToken.None);
                var token = cts.Token;  // read once: a newer click disposes cts
                var whole = _wholeType;
                var dec = await DecompilerFor(path).ConfigureAwait(true);
                var text = await Task.Run(() => whole ? dec.DecompileDeclaringType(method.Token, token) : dec.Decompile(method.Token, token), token)
                    .ConfigureAwait(true);
                if (run != _codeRun) return;
                CodeText = text;
                CodeNote = Loc.Get("Code.Source.Net");
            }
            else if (item.Proc is { } proc)
            {
                // NOT cancelled by the next click: the first request may be the one waiting for Ghidra's whole-file analysis,
                // and cancelling it would make REA throw that analysis away. A newer click just ignores this result.
                var view = _nativeView;
                var ct = _targetCts?.Token ?? CancellationToken.None;
                var code = await Task.Run(() => Backend.NativeCodeAsync(path, proc.Address, view, ct), ct).ConfigureAwait(true);
                if (run != _codeRun) return;
                _nativeCode = code;
                CodeText = code.Text ?? "";
                CodeNote = code.Text is { Length: > 0 }
                    ? Loc.Format(view == NativeCodeView.Assembly ? "Code.Source.Asm" : "Code.Source.Pseudo", code.Provider)
                    : Loc.Get("Code.Empty");
                RebuildSelection();
                refsNext = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (run == _codeRun) RefsBusy = false;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Code view failed: " + path + " " + item.Key, ex);
            if (run != _codeRun) return;
            CodeText = "";
            CodeNote = Loc.Get(CodeErrorKey(ex));
            // The decompiler can fail on one function while its references are fine. A dead or timed-out engine, a missing one
            // or an address it does not know would only fail again (or restart the whole analysis), so the refs are not asked;
            // refs already shown for this function (from the other code view) stay.
            refsNext = ex is AnalysisFailure { Kind: AnalysisFailureKind.Generic or AnalysisFailureKind.TooLarge };
            if (!refsNext && native is not null && !(_refsFor == native.Address && _nativeRefs is not null))
                ShowRefs(native.Address, null,
                    ex is AnalysisFailure { Kind: AnalysisFailureKind.InputRejected or AnalysisFailureKind.NotSupported } ? "Nat.Refs.NoCode" : "Nat.Refs.Err");
        }
        finally
        {
            if (run == _codeRun) CodeBusy = false;
        }
        // after the code, never before it: requests on the engine session run one at a time
        if (refsNext && run == _codeRun && native is not null) await LoadRefsAsync(run, native, path).ConfigureAwait(true);
    }

    // ───────── refs (native) ─────────

    /// <summary>Known refs show at once (a function opened before, or the other code view); otherwise the card says it is busy.</summary>
    private void BeginRefs(NativeProcedure proc)
    {
        if (_refsFor == proc.Address && _nativeRefs is not null) return;
        if (_refsCache.TryGetValue(proc.Address, out var known) && known.IsCompletedSuccessfully)
            ShowRefs(proc.Address, known.Result, null);
        else
            ShowRefs(proc.Address, null, "Nat.Refs.Loading", busy: true);
    }

    /// <summary>
    /// Asks the engine once per function. Like the code request it is NOT cancelled by the next click (only by another file): a
    /// result for an older selection is kept in the cache and simply not shown.
    /// </summary>
    private async Task LoadRefsAsync(int run, NativeProcedure proc, string path)
    {
        if (_refsFor == proc.Address && _nativeRefs is not null) return;
        var cache = _refsCache;
        // a failed task may still be here for a moment (its own continuation removes it): never reuse one
        if (!cache.TryGetValue(proc.Address, out var task) || task.IsFaulted || task.IsCanceled)
        {
            var ct = _targetCts?.Token ?? CancellationToken.None;
            task = Task.Run(() => Backend.NativeRefsAsync(path, proc.Address, ct), ct);
            cache[proc.Address] = task;
        }
        try
        {
            var refs = await task.ConfigureAwait(true);
            if (run != _codeRun) return;
            ShowRefs(proc.Address, refs, null);
        }
        catch (Exception ex)
        {
            // a failed lookup is not kept: picking the function again asks once more
            if (cache.TryGetValue(proc.Address, out var t) && t == task) cache.Remove(proc.Address);
            if (ex is OperationCanceledException)
            {
                if (run == _codeRun) RefsBusy = false;
                return;
            }
            AppPaths.Log("Native refs failed: " + path + " " + proc.Address, ex);
            if (run != _codeRun) return;
            ShowRefs(proc.Address, null, "Nat.Refs.Err");
        }
    }

    private void ShowRefs(string? address, NativeRefs? refs, string? noteKey, bool busy = false)
    {
        _refsFor = address;
        _nativeRefs = refs;
        _refsNoteKey = noteKey;
        RefsBusy = busy;
        RebuildSelection();
        Raise(nameof(NoRefsText));
    }

    private void OpenRef(RefRow? row)
    {
        if (row?.Target is not { } target || !_byAddress.TryGetValue(target.Key, out var item)) return;
        if (!_view.Contains(item)) Search = "";  // the filter hides it: show the whole list again
        Tab = BinTab.Code;
        Selected = item;
    }

    private static string CodeErrorKey(Exception ex) => ex switch
    {
        DecompileFailure { Kind: DecompileFailureKind.NotManaged } => "Code.Err.NotManaged",
        DecompileFailure { Kind: DecompileFailureKind.BadToken } => "Code.Err.Stale",
        DecompileFailure => "Code.Err.Failed",
        IOException or UnauthorizedAccessException => "Code.Err.Read",
        AnalysisFailure { Kind: AnalysisFailureKind.InputRejected } => "Code.Err.NoFunction",
        AnalysisFailure { Kind: AnalysisFailureKind.NotSupported } => "Code.Err.NoEngine",
        AnalysisFailure f when f.Kind != AnalysisFailureKind.Generic => "Err." + f.Kind,
        _ => "Err.Analysis",
    };

    private void CopyCode()
    {
        if (_codeText.Length == 0) return;
        try { System.Windows.Clipboard.SetText(_codeText); }
        catch (System.Runtime.InteropServices.ExternalException ex) { AppPaths.Log("Clipboard busy", ex); }
    }

    // ───────── export (.NET → C# project) ─────────

    private async Task ExportAsync()
    {
        var path = TargetPath;
        if (path is null || !IsDotNet || _exportBusy) return;
        var parent = Dialogs.PickFolder(Loc.Get("Dlg.PickExportFolder"));
        if (parent is null) return;

        // always a folder that did not exist: nothing of the user's is overwritten, and a cancelled export can be removed whole
        var target = DotNetDecompiler.FreshExportFolder(parent, Path.GetFileName(path));
        var cts = _exportCts = new CancellationTokenSource();
        ExportBusy = true;
        ExportText = Loc.Get("Code.Exporting");
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (ReferenceEquals(_exportCts, cts) && p.Total > 0) ExportText = Loc.Format("Code.ExportProgress", p.Done, p.Total);
        });
        try
        {
            var dec = await DecompilerFor(path).ConfigureAwait(true);
            await Task.Run(() => dec.ExportProject(target, progress, cts.Token), cts.Token).ConfigureAwait(true);
            ExportText = Loc.Format("Code.Exported", target);
            try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        catch (OperationCanceledException)
        {
            RemovePartial(target);
            ExportText = Loc.Get("Code.ExportCancelled");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Export failed: " + path + " → " + target, ex);
            // An Io failure may be the "folder is not empty" guard (someone created it in between): then it is not ours to
            // delete, so files already written stay and the message points at the folder.
            var io = ex is DecompileFailure { Kind: DecompileFailureKind.Io } or IOException or UnauthorizedAccessException;
            if (!io) RemovePartial(target);
            ExportText = io ? Loc.Format("Code.ExportIo", target) : Loc.Get("Code.ExportFailed");
        }
        finally
        {
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose();
            ExportBusy = false;
        }
    }

    /// <summary>Only ever called on the folder FreshExportFolder just made for this export.</summary>
    private static void RemovePartial(string target)
    {
        try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("Could not remove partial export " + target, ex); }
    }
}
