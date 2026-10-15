using System.Globalization;
using System.Text;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core.Analysis;
using ReverseX.Core.Analysis.Local;
using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

public sealed record InsightRow(InsightTone Tone, EvidenceLevel Level, string Badge, string Text);

/// <summary>A page whose findings show up on the Evidence page.</summary>
public interface IEvidenceSource
{
    string EvidenceSourceName { get; }
    string? EvidenceTarget { get; }
    IReadOnlyList<EvidenceRow> EvidenceItems { get; }
}

/// <summary>Which page this is: nav key, the local analysis it runs, its icon and the file picker filter.</summary>
public sealed record InsightSection(string Key, AnalysisKind Kind, string Icon, string Filter)
{
    public static readonly InsightSection Android = new("Android", AnalysisKind.Android, "android.png", "Android|*.apk;*.xapk;*.apks;*.apkm");
    public static readonly InsightSection Web = new("Web", AnalysisKind.Web, "web.png", "HAR / WebAssembly|*.har;*.wasm");
    public static readonly InsightSection Db = new("Db", AnalysisKind.Db, "database.png", "SQLite|*.db;*.sqlite;*.sqlite3;*.db3");
    public static readonly InsightSection Dotnet = new("Dotnet", AnalysisKind.DotNetMeta, "dotnet.png", ".NET|*.dll;*.exe");
}

public delegate Task<AnalysisSnapshot> LocalAnalyze(string path, AnalysisKind kind, IProgress<AnalysisProgress>? progress, CancellationToken ct);

/// <summary>
/// One page for everything Reverse X reads itself (APK, SQLite, HAR, WASM, .NET metadata): stat cards,
/// tables in tabs and the reader's insights with evidence levels. No REA process is involved.
/// </summary>
public sealed class InsightViewModel : AnalysisPageBase, IEvidenceSource
{
    private readonly LocalAnalyze _analyze;
    private readonly Func<string, Task>? _open;
    private IReadOnlyList<InsightTab> _tabs = Array.Empty<InsightTab>();
    private InsightTab? _tab;

    public InsightViewModel(InsightSection section, IAnalysisBackend backend, SnapshotStore snapshots, Func<string, Task>? openPath, LocalAnalyze? analyze = null)
        : base(backend, snapshots, openPath)
    {
        Section = section;
        _open = openPath;
        _analyze = analyze ?? LocalAnalyzer.AnalyzeAsync;
        PickFileCommand = new RelayCommand(() =>
        {
            var p = Dialogs.PickFile(Loc.Get("Dlg.PickFile"), Section.Filter + "|" + Loc.Get("Dlg.AllFiles") + "|*.*");
            if (p is not null && _open is not null) _ = _open(p);
        });
        SelectTabCommand = new RelayCommand(o => { if (o is InsightTab t) Tab = t; });
    }

    public InsightSection Section { get; }
    public ICommand PickFileCommand { get; }
    public ICommand SelectTabCommand { get; }

    protected override string TitleKey => "Nav." + Section.Key;
    protected override string? SnapshotSlot => Section.Kind.ToString().ToLowerInvariant();
    protected override bool Accepts(AnalysisKind saved, FileKind target) => saved == Section.Kind;

    protected override Task<AnalysisSnapshot> AnalyzeCoreAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct) =>
        _analyze(path, Section.Kind, progress, ct);

    protected override string ErrorKeyFor(Exception ex) =>
        ex is AnalysisFailure { Kind: AnalysisFailureKind.InputRejected } ? "Err.LocalRejected" : base.ErrorKeyFor(ex);

    public string Icon => "/ReverseX;component/Assets/Icons3D/" + Section.Icon;
    public string EmptyBody => Loc.Get("Page." + Section.Key + ".Desc");
    public bool HasTarget => TargetPath is not null;
    public bool ShowEmpty => IsIdle && !HasTarget;

    public string Subtitle { get; private set; } = "";
    public IReadOnlyList<StatCard> Stats { get; private set; } = Array.Empty<StatCard>();
    public IReadOnlyList<InsightRow> Insights { get; private set; } = Array.Empty<InsightRow>();
    public bool HasInsights => Insights.Count > 0;
    public IReadOnlyList<InsightTab> Tabs { get => _tabs; private set => Set(ref _tabs, value); }

    public InsightTab? Tab
    {
        get => _tab;
        set
        {
            if (_tab is not null) _tab.IsSelected = false;
            Set(ref _tab, value);
            if (value is not null) value.IsSelected = true;
        }
    }

    // ───────── evidence source ─────────

    public string EvidenceSourceName => Loc.Get("Nav." + Section.Key);
    public string? EvidenceTarget => IsReady ? TargetPath : null;
    public IReadOnlyList<EvidenceRow> EvidenceItems => Insights.Select(i => new EvidenceRow(i.Level, i.Badge, i.Text)).ToList();

    // ───────── apply ─────────

    protected override void ClearResult()
    {
        Subtitle = "";
        Stats = Array.Empty<StatCard>();
        Insights = Array.Empty<InsightRow>();
        Tab = null;
        Tabs = Array.Empty<InsightTab>();
        RaiseAll();
    }

    protected override void ApplySnapshot(AnalysisSnapshot snap)
    {
        Build(snap, keepTab: null);
        RaiseAll();
    }

    protected override void OnStateChanged() => Raise(nameof(ShowEmpty));

    protected override void OnLanguageChanged()
    {
        Raise(nameof(EmptyBody));
        if (Snapshot is null) return;
        Build(Snapshot, keepTab: _tab?.Key);
        RaiseAll();
    }

    private void Build(AnalysisSnapshot snap, string? keepTab)
    {
        List<InsightTab> tabs = new();
        List<Insight> insights = new();
        Subtitle = "";
        Stats = Array.Empty<StatCard>();
        if (snap.Android is { } a) { BuildAndroid(a, tabs); insights = a.Insights; }
        else if (snap.Db is { } d) { BuildDb(d, tabs); insights = d.Insights; }
        else if (snap.Har is { } h) { BuildHar(h, tabs); insights = h.Insights; }
        else if (snap.Wasm is { } w) { BuildWasm(w, tabs); insights = w.Insights; }
        else if (snap.NetMeta is { } n) { BuildNet(n, tabs); insights = n.Insights; }

        Insights = insights
            .OrderBy(i => i.Tone switch { InsightTone.Risk => 0, InsightTone.Warn => 1, InsightTone.Info => 2, _ => 3 })
            .Select(i => new InsightRow(i.Tone, i.Level, Badge(i.Level), InsightText(i)))
            .ToList();
        var shown = tabs.Where(t => t.IsText || t.Count > 0).ToList();
        if (shown.Count == 0 && tabs.Count > 0) shown.Add(tabs[0]);
        Tab = null;
        Tabs = shown;
        Tab = shown.FirstOrDefault(t => t.Key == keepTab) ?? shown.FirstOrDefault();
    }

    public static string Badge(EvidenceLevel lv) =>
        Loc.Get(lv switch { EvidenceLevel.Seen => "Ev.Seen", EvidenceLevel.Inferred => "Ev.Inferred", _ => "Ev.Unknown" });

    /// <summary>Fills "Ins.{Code}" with the insight's args. Missing args become empty so a short list never throws.</summary>
    public static string InsightText(Insight i)
    {
        var fmt = Loc.Get("Ins." + i.Code);
        var args = i.Args.Cast<object>().Concat(Enumerable.Repeat<object>("", 6)).ToArray();
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return fmt; }
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.CurrentCulture);
    private static string C(string key) => Loc.Get("Ins.Col." + key);
    private static string[] Cols(params string[] keys) => keys.Select(C).ToArray();
    private static string Sha(string s) => s.Length >= 16 ? s[..8] + "…" + s[^8..] : s;

    // ───────── Android ─────────

    private void BuildAndroid(AndroidModel m, List<InsightTab> tabs)
    {
        var parts = new List<string> { m.PackageName };
        if (m.Label is { Length: > 0 }) parts.Insert(0, m.Label);
        if (m.VersionName is not null) parts.Add("v" + m.VersionName + (m.VersionCode is null ? "" : $" ({m.VersionCode})"));
        if (m.Framework.Length > 0) parts.Add(m.Framework);
        if (m.Splits.Count > 0) parts.Add(Loc.Format("Ins.Splits", m.Splits.Count));
        Subtitle = string.Join(" · ", parts);

        var dangerous = m.Permissions.Count(p => p.Protection == "dangerous");
        Stats = new[]
        {
            new StatCard(Loc.Get("Ins.Stat.Sdk"), $"{m.MinSdk?.ToString(CultureInfo.InvariantCulture) ?? "?"}–{m.TargetSdk?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                Loc.Format("Ins.Stat.SdkSub", m.CompileSdk?.ToString(CultureInfo.InvariantCulture) ?? "?")),
            new StatCard(Loc.Get("Ins.Stat.Perms"), N(m.Permissions.Count), Loc.Format("Ins.Stat.PermsSub", dangerous)),
            new StatCard(Loc.Get("Ins.Stat.Components"), N(m.Components.Count), Loc.Format("Ins.Stat.ComponentsSub", m.Components.Count(c => c.Exported))),
            new StatCard(Loc.Get("Ins.Stat.Classes"), N(m.ClassCount), Loc.Format("Ins.Stat.ClassesSub", m.DexFiles.Count)),
            new StatCard(Loc.Get("Ins.Stat.Size"), ByteSize.Format(m.UncompressedSize), Loc.Format("Ins.Stat.FilesSub", N(m.FileCount))),
        };

        tabs.Add(new InsightTab("Permissions", Cols("Name", "Protection"),
            m.Permissions.Select(p => new object?[] { p.Name, p.Protection }).ToList()));
        tabs.Add(new InsightTab("Components", Cols("Type", "Name", "Exported", "Permission", "Actions"),
            m.Components.Select(c => new object?[] { c.Type, c.Name, c.Exported, c.Permission, string.Join(", ", c.Actions.Select(x => x.Split('.').Last())) }).ToList(),
            i =>
            {
                var c = m.Components[i];
                var sb = new StringBuilder().AppendLine($"{c.Type}  {c.Name}");
                if (c.Permission is not null) sb.AppendLine("permission: " + c.Permission);
                foreach (var x in c.Actions) sb.AppendLine("action:   " + x);
                foreach (var x in c.Categories) sb.AppendLine("category: " + x);
                foreach (var x in c.Data) sb.AppendLine("data:     " + x);
                return sb.ToString();
            }));
        tabs.Add(new InsightTab("Sdks", Cols("Name", "Category", "Classes"),
            m.Sdks.Select(s => new object?[] { s.Name, s.Category, s.Classes }).ToList()));
        tabs.Add(new InsightTab("Packages", Cols("Package", "Classes"),
            m.Packages.Select(p => new object?[] { p.Name, p.Count }).ToList()));
        tabs.Add(new InsightTab("Classes", Cols("Class"), m.Classes.Select(c => new object?[] { c }).ToList()));
        tabs.Add(new InsightTab("Native", Cols("Abi", "Name", "Size"),
            m.NativeLibs.Select(l => new object?[] { l.Abi, l.Name, l.Size }).ToList()));
        tabs.Add(new InsightTab("Certs", Cols("Scheme", "Subject", "ValidUntil", "Algorithm", "Sha"),
            m.Certificates.Select(c => new object?[] { c.Scheme, c.Subject, c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.Algorithm, Sha(c.Sha256) }).ToList(),
            i =>
            {
                var c = m.Certificates[i];
                return $"{c.Scheme}\nsubject: {c.Subject}\nissuer:  {c.Issuer}\nvalid:   {c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd}\nalgo:    {c.Algorithm} · {c.KeyInfo}\nSHA-256: {c.Sha256}";
            }));
        tabs.Add(new InsightTab("Urls", Cols("Url"), m.Urls.Concat(m.Schemes.Select(s => s + "://")).Select(u => new object?[] { u }).ToList()));
        tabs.Add(new InsightTab("Secrets", Cols("Kind", "Value", "Where"), m.Secrets.Select(s => new object?[] { s.Kind, s.Masked, s.Where }).ToList()));
        tabs.Add(new InsightTab("Files", Cols("Folder", "Files"), m.TopFolders.Select(f => new object?[] { f.Name, f.Count }).ToList()));
        if (m.ManifestXml.Length > 0) tabs.Add(new InsightTab("Manifest", m.ManifestXml));
    }

    // ───────── SQLite ─────────

    private void BuildDb(DbModel m, List<InsightTab> tabs)
    {
        Subtitle = string.Join(" · ", new[] { "SQLite " + m.WriterVersion, m.TextEncoding, m.UserVersion != 0 ? "user_version " + m.UserVersion : "" }
            .Concat(m.Origins).Where(s => s.Trim().Length > 0));

        var tables = m.Tables.Where(t => t.Type == "table").ToList();
        var biggest = tables.Where(t => t.RowCount is not null).OrderByDescending(t => t.RowCount).FirstOrDefault();
        Stats = new[]
        {
            new StatCard(Loc.Get("Ins.Stat.Tables"), N(tables.Count), Loc.Format("Ins.Stat.TablesSub", m.Tables.Count(t => t.Type == "view"))),
            new StatCard(Loc.Get("Ins.Stat.Rows"), N(tables.Sum(t => t.RowCount ?? 0)), biggest is null ? "" : Loc.Format("Ins.Stat.RowsSub", biggest.Name)),
            new StatCard(Loc.Get("Ins.Stat.Pages"), N(m.PageCount), Loc.Format("Ins.Stat.PagesSub", N(m.PageSize), ByteSize.Format(m.PageCount * m.PageSize))),
            new StatCard(Loc.Get("Ins.Stat.Wal"), m.WalMode || m.WalBytes > 0 ? ByteSize.Format(m.WalBytes) : "—",
                Loc.Get(m.WalIncluded ? "Ins.Stat.WalIn" : "Ins.Stat.WalOut")),
            new StatCard(Loc.Get("Ins.Stat.Triggers"), N(m.Triggers.Count), m.EngineVersion.Length > 0 ? Loc.Format("Ins.Stat.Engine", m.EngineVersion) : ""),
        };

        tabs.Add(new InsightTab("Tables", Cols("Name", "Type", "Rows", "Columns", "Indexes", "Sensitive"),
            m.Tables.Select(t => new object?[] { t.Name, t.Type, t.RowCount, t.Columns.Count, t.Indexes.Count, string.Join(", ", t.SensitiveColumns) }).ToList(),
            i => TableDetail(m.Tables[i])));
        tabs.Add(new InsightTab("Columns", Cols("Table", "Column", "Type", "Pk", "NotNull", "Default"),
            m.Tables.SelectMany(t => t.Columns.Select(c => new object?[] { t.Name, c.Name, c.Type, c.PrimaryKey, c.NotNull, c.Default })).ToList()));
        if (m.TriggerSql.Count > 0) tabs.Add(new InsightTab("Triggers", string.Join("\n\n", m.TriggerSql)));
    }

    private static string TableDetail(DbTable t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(t.Sql.Length > 0 ? t.Sql : t.Name);
        foreach (var fk in t.ForeignKeys) sb.AppendLine("FK " + fk);
        foreach (var ix in t.Indexes) sb.AppendLine("INDEX " + ix);
        if (t.Sample.Count > 0)
        {
            sb.AppendLine().AppendLine(Loc.Format("Ins.Sample", t.Sample.Count));
            sb.AppendLine(string.Join(" | ", t.SampleColumns));
            foreach (var row in t.Sample) sb.AppendLine(string.Join(" | ", row.Select(v => v ?? "NULL")));
        }
        return sb.ToString();
    }

    // ───────── HAR ─────────

    private void BuildHar(HarModel m, List<InsightTab> tabs)
    {
        Subtitle = string.Join(" · ", new[] { "HAR", m.Creator, m.Browser ?? "" }.Where(s => s.Length > 0));
        Stats = new[]
        {
            new StatCard(Loc.Get("Ins.Stat.Requests"), N(m.TotalEntries), Loc.Format("Ins.Stat.RequestsSub", m.Pages)),
            new StatCard(Loc.Get("Ins.Stat.Hosts"), N(m.Hosts.Count), Loc.Format("Ins.Stat.HostsSub", m.Hosts.Count(h => h.Tracker is not null))),
            new StatCard(Loc.Get("Ins.Stat.Endpoints"), N(m.Endpoints.Count), Loc.Format("Ins.Stat.HostsTls", m.Hosts.Count(h => h.Tls))),
            new StatCard(Loc.Get("Ins.Stat.Errors"), N(m.Errors4xx + m.Errors5xx), Loc.Format("Ins.Stat.ErrorsSub", m.Errors4xx, m.Errors5xx)),
            new StatCard(Loc.Get("Ins.Stat.Transfer"), ByteSize.Format(m.TotalBytes), Loc.Format("Ins.Stat.TransferSub", (m.SpanMs / 1000).ToString("0.#", CultureInfo.CurrentCulture))),
        };

        tabs.Add(new InsightTab("Requests", Cols("Index", "Method", "Status", "Host", "Path", "Mime", "Size", "StartMs", "TimeMs"),
            m.Entries.Select(e => new object?[] { e.Index, e.Method, e.Status, e.Host, e.Path, e.MimeType, e.ResponseBytes, Math.Round(e.StartMs), Math.Round(e.TimeMs) }).ToList(),
            i => RequestDetail(m.Entries[i])));
        tabs.Add(new InsightTab("Endpoints", Cols("Method", "Host", "Template", "Count", "Errors", "AvgMs", "Mime"),
            m.Endpoints.Select(e => new object?[] { e.Method, e.Host, e.Template, e.Count, e.Errors, Math.Round(e.AvgMs), e.MimeType }).ToList()));
        tabs.Add(new InsightTab("Hosts", Cols("Host", "Requests", "Bytes", "Tls", "Errors", "Tracker"),
            m.Hosts.Select(h => new object?[] { h.Host, h.Requests, h.Bytes, h.Tls, h.Errors, h.Tracker }).ToList()));
        tabs.Add(new InsightTab("Mime", Cols("Mime", "Count"), m.MimeTypes.Select(t => new object?[] { t.Name, t.Count }).ToList()));
    }

    private static string RequestDetail(HarEntry e)
    {
        const int maxBody = 20_000;
        static string Body(string s) => s.Length > maxBody ? s[..maxBody] + "\n…" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Method} {e.Url}");
        sb.AppendLine($"→ {e.Status} {e.StatusText} · {e.HttpVersion} · {e.ServerIp} · {e.TimeMs.ToString("0", CultureInfo.InvariantCulture)} ms");
        if (e.Query.Count > 0) { sb.AppendLine().AppendLine("# query"); foreach (var q in e.Query) sb.AppendLine($"{q.Name} = {q.Value}"); }
        sb.AppendLine().AppendLine("# request headers");
        foreach (var h in e.RequestHeaders) sb.AppendLine($"{h.Name}: {h.Value}");
        if (e.RequestBody is { Length: > 0 } rb) sb.AppendLine().AppendLine("# request body").AppendLine(Body(rb));
        sb.AppendLine().AppendLine("# response headers");
        foreach (var h in e.ResponseHeaders) sb.AppendLine($"{h.Name}: {h.Value}");
        if (e.ResponseBody is { Length: > 0 } body) sb.AppendLine().AppendLine("# response body").AppendLine(Body(body));
        return sb.ToString();
    }

    // ───────── WASM ─────────

    private void BuildWasm(WasmModel m, List<InsightTab> tabs)
    {
        Subtitle = string.Join(" · ", new[] { "WebAssembly v" + m.Version, m.Toolchain }.Where(s => s.Length > 0));
        string Kinds<T>(IEnumerable<T> items, Func<T, string> kind) =>
            string.Join(" · ", items.GroupBy(kind).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} {g.Count()}"));
        Stats = new[]
        {
            new StatCard(Loc.Get("Ins.Stat.Functions"), N(m.DefinedFunctions), Loc.Format("Ins.Stat.FunctionsSub", m.ImportedFunctions)),
            new StatCard(Loc.Get("Ins.Stat.Imports"), N(m.Imports.Count), string.Join(", ", m.Imports.Select(i => i.Module).Distinct().Take(3))),
            new StatCard(Loc.Get("Ins.Stat.Exports"), N(m.Exports.Count), Kinds(m.Exports, e => e.Kind)),
            new StatCard(Loc.Get("Ins.Stat.Memory"), m.MemoryMinPages is { } min ? ByteSize.Format((long)Math.Min(min * 65536, long.MaxValue / 2)) : "—",
                m.MemoryMaxPages is { } max ? Loc.Format("Ins.Stat.MemoryMax", ByteSize.Format((long)Math.Min(max * 65536, long.MaxValue / 2))) : Loc.Get("Ins.Stat.MemoryNoMax")),
            new StatCard(Loc.Get("Ins.Stat.Toolchain"), m.Toolchain.Length > 0 ? m.Toolchain : "?", m.Producers.FirstOrDefault() ?? ""),
        };

        tabs.Add(new InsightTab("Imports", Cols("Module", "Name", "Kind"), m.Imports.Select(i => new object?[] { i.Module, i.Name, i.Kind }).ToList()));
        tabs.Add(new InsightTab("Exports", Cols("Name", "Kind", "Index"), m.Exports.Select(e => new object?[] { e.Name, e.Kind, (long)e.Index }).ToList()));
        tabs.Add(new InsightTab("Sections", Cols("Section", "Bytes"), m.Sections.Select(s => new object?[] { s.Name, s.Count }).ToList()));
        tabs.Add(new InsightTab("Functions", Cols("Name"), m.FunctionNames.Select(f => new object?[] { f }).ToList()));
        tabs.Add(new InsightTab("Strings", Cols("Text"), m.Strings.Select(s => new object?[] { s }).ToList()));
        if (m.Producers.Count > 0) tabs.Add(new InsightTab("Producers", string.Join("\n", m.Producers)));
    }

    // ───────── .NET metadata ─────────

    private void BuildNet(NetMetaModel m, List<InsightTab> tabs)
    {
        Subtitle = string.Join(" · ", new[] { m.Name + " " + m.Version, m.TargetFramework ?? "", m.Machine, m.IsDll ? "DLL" : "EXE" }.Where(s => s.Trim().Length > 0));
        Stats = new[]
        {
            new StatCard(Loc.Get("Ins.Stat.Types"), N(m.Types.Count), Loc.Format("Ins.Stat.TypesSub", m.Types.Count(t => t.Public))),
            new StatCard(Loc.Get("Ins.Stat.Methods"), N(m.MethodCount), Loc.Format("Ins.Stat.MethodsSub", m.PInvokes.Count)),
            new StatCard(Loc.Get("Ins.Stat.Refs"), N(m.References.Count), string.Join(", ", m.References.Select(r => r.Name).Take(3))),
            new StatCard(Loc.Get("Ins.Stat.Resources"), N(m.Resources.Count), ByteSize.Format(m.Resources.Sum(r => r.Size))),
            new StatCard(Loc.Get("Ins.Stat.Framework"), m.TargetFramework ?? "?",
                string.Join(" · ", new[] { m.ReadyToRun ? "ReadyToRun" : "", m.ILOnly ? "IL only" : "mixed", m.Obfuscator ?? "" }.Where(s => s.Length > 0))),
        };

        var info = new List<object?[]>
        {
            new object?[] { Loc.Get("Meta.Name"), m.Name },
            new object?[] { Loc.Get("Meta.Version"), m.Version },
            new object?[] { Loc.Get("Meta.Framework"), m.TargetFramework },
            new object?[] { Loc.Get("Meta.Arch"), m.Machine },
            new object?[] { Loc.Get("Meta.Kind"), m.IsDll ? "DLL" : "EXE" },
            new object?[] { Loc.Get("Meta.Mvid"), m.Mvid },
            new object?[] { Loc.Get("Meta.Metadata"), m.MetadataVersion },
            new object?[] { "Culture", m.Culture },
            new object?[] { "PublicKeyToken", m.PublicKeyToken },
            new object?[] { "Strong name", m.StrongNameSigned },
            new object?[] { "IL only", m.ILOnly },
            new object?[] { "ReadyToRun", m.ReadyToRun },
            new object?[] { "Entry point", m.EntryPoint },
            new object?[] { "Company", m.Company },
            new object?[] { "Product", m.Product },
            new object?[] { "Title", m.Title },
            new object?[] { "Copyright", m.Copyright },
            new object?[] { "InformationalVersion", m.InformationalVersion },
            new object?[] { "Obfuscator", m.Obfuscator },
            new object?[] { "InternalsVisibleTo", string.Join(", ", m.InternalsVisibleTo) },
        };
        tabs.Add(new InsightTab("Info", Cols("Field", "Value"), info.Where(r => r[1] is not null and not "").ToList()));
        tabs.Add(new InsightTab("Types", Cols("Namespace", "Name", "Kind", "Public", "Methods", "Fields", "Props", "Base"),
            m.Types.Select(t => new object?[] { t.Namespace, t.Name, t.Kind, t.Public, t.Methods, t.Fields, t.Properties, t.BaseType }).ToList()));
        tabs.Add(new InsightTab("Refs", Cols("Name", "Version", "Token"), m.References.Select(r => new object?[] { r.Name, r.Version, r.PublicKeyToken }).ToList()));
        tabs.Add(new InsightTab("PInvoke", Cols("Module", "Entry", "Method"), m.PInvokes.Select(p => new object?[] { p.Module, p.Entry, p.Method }).ToList()));
        tabs.Add(new InsightTab("Api", Cols("Category", "Api", "Count"), m.ApiUse.Select(a => new object?[] { a.Category, a.Api, a.Count }).ToList()));
        tabs.Add(new InsightTab("Resources", Cols("Name", "Size", "Public"), m.Resources.Select(r => new object?[] { r.Name, r.Size, r.Public }).ToList()));
        tabs.Add(new InsightTab("Urls", Cols("Url"), m.Urls.Select(u => new object?[] { u }).ToList()));
        tabs.Add(new InsightTab("Secrets", Cols("Kind", "Value", "Where"), m.Secrets.Select(s => new object?[] { s.Kind, s.Masked, s.Where }).ToList()));
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(HasTarget), nameof(ShowEmpty), nameof(Subtitle), nameof(Stats), nameof(Insights), nameof(HasInsights),
            nameof(Tabs), nameof(Tab), nameof(TargetName), nameof(EmptyBody),
        }) Raise(n);
    }
}
