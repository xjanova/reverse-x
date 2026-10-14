using System.Text.Json.Nodes;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.Core.Analysis;

public enum AnalysisFailureKind
{
    Timeout,
    /// <summary>The result is too large for the MCP transport and the CLI fallback is unavailable or failed.</summary>
    TooLarge,
    /// <summary>The file is not something this analysis can read (e.g. not a managed assembly).</summary>
    NotSupported,
    InputRejected,
    /// <summary>REA could not be started or died.</summary>
    ReaUnavailable,
    /// <summary>Node.js is missing or too old, so REA cannot run at all.</summary>
    NoNode,
    Generic,
}

/// <summary>A problem the UI turns into a Thai/English sentence — the raw detail only goes to the log.</summary>
public sealed class AnalysisFailure : Exception
{
    public AnalysisFailureKind Kind { get; }
    public AnalysisFailure(AnalysisFailureKind kind, string detail) : base(detail) => Kind = kind;
}

/// <param name="Step">Step key such as net.artifact, js.modules, native.open (the UI maps it to text).</param>
public sealed record AnalysisProgress(string Step, int Done, int Total);

/// <summary>Seam between the runner and the REA process, so tests can use recorded fixtures instead of Node.</summary>
public interface IReaToolCaller
{
    Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs one REA analysis per project kind and returns a snapshot. Cancellation is honoured between and during calls.</summary>
public sealed class AnalysisRunner
{
    public const int ModulePageSize = 250;
    public const int ModuleCap = 5000;
    public const int ProcedureCap = 20000;
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    private readonly IReaToolCaller _rea;
    private readonly Func<string, CancellationToken, Task<string>>? _managedMembersCli;

    /// <param name="managedMembersCli">Fallback returning inspect-managed-members JSON through the CLI (no MCP size budget).</param>
    public AnalysisRunner(IReaToolCaller rea, Func<string, CancellationToken, Task<string>>? managedMembersCli = null)
    {
        _rea = rea;
        _managedMembersCli = managedMembersCli;
    }

    public async Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var (size, write) = SnapshotStore.Stamp(path);
        var snap = new AnalysisSnapshot
        {
            SourcePath = Path.GetFullPath(path),
            SourceSize = size,
            SourceWriteUtc = write,
            CreatedUtc = DateTime.UtcNow,
            ReaVersion = ReaLauncher.PinnedVersion,
        };
        try
        {
            switch (kind)
            {
                case FileKind.DotNet:
                    return snap with { Kind = AnalysisKind.DotNet, DotNet = await AnalyzeDotNetAsync(path, progress, ct).ConfigureAwait(false) };
                case FileKind.AppFolder:
                case FileKind.Folder:
                case FileKind.Asar:
                    return snap with { Kind = AnalysisKind.Js, Js = await AnalyzeJsAsync(path, progress, ct).ConfigureAwait(false) };
                case FileKind.Pe:
                case FileKind.Elf:
                case FileKind.MachO:
                    var (native, go) = await AnalyzeNativeAsync(path, progress, ct).ConfigureAwait(false);
                    return snap with { Kind = AnalysisKind.Native, Native = native, Go = go };
                default:
                    throw new AnalysisFailure(AnalysisFailureKind.NotSupported, "kind " + kind);
            }
        }
        catch (TimeoutException ex)
        {
            throw new AnalysisFailure(AnalysisFailureKind.Timeout, ex.Message);
        }
        catch (ReaException ex)
        {
            throw new AnalysisFailure(AnalysisFailureKind.ReaUnavailable, ex.Message);
        }
    }

    // ───────────── helpers ─────────────

    private async Task<string> CallTextAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await _rea.CallAsync(tool, args, timeout, ct).ConfigureAwait(false);
        return r.IsError ? throw Fail(tool, r.Text) : r.Text;
    }

    private static AnalysisFailure Fail(string tool, string text)
    {
        var err = ReaResultParser.ParseError(text);
        var kind = err?.Code switch
        {
            "resource_constraint" => AnalysisFailureKind.TooLarge,
            "invalid_input" or "invalid_request" => AnalysisFailureKind.InputRejected,
            "capability_unavailable" or "provider_unavailable" => AnalysisFailureKind.NotSupported,
            _ => AnalysisFailureKind.Generic,
        };
        return new AnalysisFailure(kind, $"{tool}: {err?.Code ?? "?"}: {err?.Message ?? text}");
    }

    private static void Report(IProgress<AnalysisProgress>? p, string step, int done, int total) =>
        p?.Report(new AnalysisProgress(step, done, total));

    // ───────────── .NET ─────────────

    private async Task<DotNetModel> AnalyzeDotNetAsync(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        Report(progress, "net.artifact", 0, 3);
        var artifact = await CallTextAsync("inspect_managed_artifact", new JsonObject { ["path"] = full }, Long, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        Report(progress, "net.members", 1, 3);
        var r = await _rea.CallAsync("inspect_managed_members", new JsonObject { ["path"] = full }, Long, ct).ConfigureAwait(false);
        string members;
        if (!r.IsError)
        {
            members = r.Text;
        }
        else if (ReaResultParser.ParseError(r.Text)?.Code == "resource_constraint" && _managedMembersCli is not null)
        {
            // Too big for the MCP transport budget: the CLI has no such limit.
            ct.ThrowIfCancellationRequested();
            Report(progress, "net.cli", 2, 3);
            try { members = await _managedMembersCli(full, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AnalysisFailure(AnalysisFailureKind.TooLarge, "CLI fallback failed: " + ex.Message);
            }
        }
        else
        {
            throw Fail("inspect_managed_members", r.Text);
        }

        ct.ThrowIfCancellationRequested();
        Report(progress, "parse", 3, 3);
        var model = await Task.Run(() => ReaResultParser.ParseManaged(artifact, members), ct).ConfigureAwait(false);
        if (model.Types.Count == 0 && string.Equals(model.MembersCoverage, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            var why = string.Join(" ", model.MembersLimitations);
            throw new AnalysisFailure(AnalysisFailureKind.TooLarge, why.Length > 0 ? why : "REA could not read the member metadata (coverage: unavailable)");
        }
        if (model.Info.Name.Length == 0 && model.Types.Count == 0)
            throw new AnalysisFailure(AnalysisFailureKind.NotSupported, "no managed metadata");
        return model;
    }

    // ───────────── JS / Electron ─────────────

    private async Task<JsAppModel> AnalyzeJsAsync(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        Report(progress, "js.analyze", 0, 0);
        // detail=summary keeps the full evidence inside REA and returns only a small summary (detail=complete busts the
        // transport budget on real apps); record-and-continue because packagers routinely strip/sign unpacked .node files
        // (REA then marks those bytes untrusted and says so in its limitations).
        var text = await CallTextAsync("analyze_javascript_application", new JsonObject
        {
            ["input_path"] = Path.GetFullPath(path),
            ["detail"] = "summary",
            ["integrity_policy"] = "record-and-continue",
        }, Long, ct).ConfigureAwait(false);
        var summary = ReaResultParser.ParseJsSummary(text);

        var modules = new List<JsModuleItem>();
        var total = 0;
        if (!string.IsNullOrEmpty(summary.ParentEvidenceId))
        {
            var offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await CallTextAsync("inspect_analysis_view", new JsonObject
                {
                    ["source"] = new JsonObject { ["kind"] = "retained-evidence", ["evidence_id"] = summary.ParentEvidenceId },
                    ["view"] = new JsonObject { ["kind"] = "page", ["collection"] = "modules", ["offset"] = offset, ["limit"] = ModulePageSize },
                }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                var (items, pageTotal, next) = ReaResultParser.ParseJsModulesPage(page);
                total = pageTotal;
                modules.AddRange(items);
                Report(progress, "js.modules", modules.Count, Math.Min(total, ModuleCap));
                if (next is null || items.Count == 0 || modules.Count >= ModuleCap) break;
                offset = next.Value;
            }
        }
        return new JsAppModel { Summary = summary, Modules = modules, ModulesTotal = Math.Max(total, modules.Count) };
    }

    // ───────────── native (+ Go) ─────────────

    private async Task<(NativeModel Native, GoBuildInfoModel? Go)> AnalyzeNativeAsync(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        Report(progress, "go.read", 0, 3);
        var go = await Task.Run(() => GoBuildInfo.TryRead(path, ct), ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        Report(progress, "native.open", 1, 3);
        var openText = await CallTextAsync("open_binary", new JsonObject { ["path"] = Path.GetFullPath(path) }, Long, ct).ConfigureAwait(false);
        var identity = ReaResultParser.ParseNativeIdentity(openText);

        ct.ThrowIfCancellationRequested();
        Report(progress, "native.procs", 2, 3);
        var r = await _rea.CallAsync("list_procedures", new JsonObject(), Long, ct).ConfigureAwait(false);
        if (r.IsError)
        {
            var err = ReaResultParser.ParseError(r.Text);
            if (err?.Code is "provider_unavailable" or "capability_unavailable")
                return (new NativeModel { Identity = identity, EngineMissing = true, Rejections = err.Rejections }, go);
            throw Fail("list_procedures", r.Text);
        }
        var all = ReaResultParser.ParseProcedures(r.Text);
        return (new NativeModel { Identity = identity, Procedures = all.Take(ProcedureCap).ToList(), ProcedureTotal = all.Count }, go);
    }

    /// <summary>Selects the binary in this REA session (needed before function-level calls).</summary>
    public async Task OpenAsync(string path, CancellationToken ct) =>
        await CallTextAsync("open_binary", new JsonObject { ["path"] = Path.GetFullPath(path) }, Long, ct).ConfigureAwait(false);

    /// <summary>Dossier for one native function (pseudo-code, assembly, callers…). Needs an engine; returns REA's JSON text.</summary>
    public Task<string> AnalyzeFunctionAsync(string procedure, CancellationToken ct) =>
        CallTextAsync("analyze_function", new JsonObject { ["procedure"] = procedure }, Long, ct);

    /// <summary>Caller/callee graph around one address. Needs an engine; returns REA's JSON text.</summary>
    public Task<string> CallGraphAsync(string address, CancellationToken ct) =>
        CallTextAsync("get_call_graph", new JsonObject { ["address"] = address }, Long, ct);
}
