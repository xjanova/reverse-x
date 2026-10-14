using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.Core.Ai;

public sealed record ToolOutput(string Text, bool IsError);

/// <summary>Which REA tools to offer for which kind of project (a read-only, static subset; never all 170).</summary>
public static class ToolSelector
{
    private static readonly string[] Common = { "inspect_artifact", "inspect_analysis_view", "list_unknowns", "inspect_binary_layout" };
    private static readonly string[] Js =
    {
        "analyze_javascript_application", "trace_application_feature", "trace_javascript_semantics", "recover_javascript_sources",
        "compare_source_to_bundle", "trace_web_module_imports", "trace_web_source_location", "inspect_web_event_listeners", "analyze_web_bundle",
    };
    private static readonly string[] Net =
    {
        "inspect_managed_artifact", "inspect_managed_members", "inspect_managed_native_boundaries", "project_managed_application_graph",
        "compare_managed_members", "inspect_pe_resources",
    };
    private static readonly string[] Native =
    {
        "open_binary", "close_binary", "binary_overview", "list_procedures", "search_procedures", "procedure_info", "procedure_pseudo_code",
        "procedure_assembly", "procedure_callers", "procedure_callees", "analyze_function", "get_call_graph", "list_strings", "search_strings",
        "list_segments", "xrefs", "trace_feature", "trace_call_path", "build_call_path", "inspect_native_api", "inspect_pe_resources",
    };
    private static readonly string[] Android =
    {
        "inspect_android_package", "search_android_classes", "inspect_android_class", "inspect_android_method", "trace_android_references",
        "project_android_application_graph", "decode_android_resources",
    };

    public static IReadOnlyList<string> NamesFor(FileKind kind)
    {
        var specific = kind switch
        {
            FileKind.Asar or FileKind.AppFolder or FileKind.Folder => Js,
            FileKind.DotNet => Net,
            FileKind.Pe or FileKind.Elf or FileKind.MachO => Native,
            FileKind.Apk => Android,
            FileKind.Wasm or FileKind.Har => Js,
            _ => Array.Empty<string>(),
        };
        return Common.Concat(specific).Distinct().ToList();
    }

    public static IReadOnlyList<ReaTool> Select(FileKind kind, IEnumerable<ReaTool> available)
    {
        var want = NamesFor(kind).ToHashSet();
        return available.Where(t => want.Contains(t.Name)).ToList();
    }
}

/// <summary>
/// Everything the model can call: the selected REA tools plus local tools (record_finding, get_result_page and, when the user chose
/// their own project folder, list/read files and propose_file_change). Big results are shrunk before they go back to the model.
/// </summary>
public sealed class AgentToolbox
{
    public const string RecordFinding = "record_finding";
    public const string GetResultPage = "get_result_page";
    public const string ListProjectFiles = "list_project_files";
    public const string ReadProjectFile = "read_project_file";
    public const string ProposeChange = "propose_file_change";

    private static readonly Regex PathKey = new(@"(^|_)(path|file|artifact|binary|source|input|asar)s?$", RegexOptions.IgnoreCase);

    private readonly IReaToolCaller _rea;
    private readonly IReadOnlyList<ReaTool> _reaTools;
    private readonly string[] _allowedRoots;
    private readonly string? _ownRoot;
    private readonly ResultStore _store = new();
    private readonly Action<AgentEvent> _emit;
    private readonly TimeSpan _reaTimeout;

    public AgentToolbox(IReaToolCaller rea, IReadOnlyList<ReaTool> reaTools, string? targetPath, string? ownProjectRoot,
        Action<AgentEvent> emit, TimeSpan? reaTimeout = null)
    {
        _rea = rea;
        _reaTools = reaTools;
        _ownRoot = string.IsNullOrWhiteSpace(ownProjectRoot) ? null : ownProjectRoot;
        _emit = emit;
        _reaTimeout = reaTimeout ?? TimeSpan.FromMinutes(5);
        _allowedRoots = Roots(targetPath);
    }

    private static string[] Roots(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return Array.Empty<string>();
        var full = Path.GetFullPath(target);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        return dir is null ? new[] { full } : new[] { full, dir };
    }

    public IReadOnlyList<AiToolDef> Definitions()
    {
        var defs = new List<AiToolDef>();
        foreach (var t in _reaTools)
            defs.Add(new AiToolDef(t.Name, Trim(t.Description ?? t.Name, 700), t.InputSchema.ValueKind == JsonValueKind.Object ? t.InputSchema.GetRawText() : EmptySchema));
        defs.Add(new AiToolDef(RecordFinding,
            "Record one finding so the user sees it in the side panel. kind=path: the route you found (steps = ordered node labels in Thai). " +
            "kind=evidence: a claim with level seen (a tool output showed it directly) or inferred (you deduced it) and location (file:line or address). " +
            "kind=unknown: something you could not establish. Write text in Thai. Never mark inferred things as seen.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["path","evidence","unknown"]},"text":{"type":"string"},"level":{"type":"string","enum":["seen","inferred"]},"location":{"type":"string"},"steps":{"type":"array","items":{"type":"string"}}},"required":["kind","text"]}"""));
        defs.Add(new AiToolDef(GetResultPage,
            "Read another page of a tool result that was shortened (use the result_id shown in the note at the end of that result).",
            """{"type":"object","properties":{"result_id":{"type":"string"},"page":{"type":"integer","minimum":1}},"required":["result_id","page"]}"""));
        if (_ownRoot is not null)
        {
            defs.Add(new AiToolDef(ListProjectFiles, "List files of the user's own project (relative paths, up to 300).", EmptySchema));
            defs.Add(new AiToolDef(ReadProjectFile, "Read a text file of the user's own project (relative path, up to 40,000 characters).",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""));
            defs.Add(new AiToolDef(ProposeChange,
                "Propose creating or replacing ONE file in the user's own project. This does NOT write anything: the user sees a diff and decides. " +
                "Give the complete new file content, a relative path, and a short Thai explanation.",
                """{"type":"object","properties":{"path":{"type":"string"},"new_content":{"type":"string"},"explanation":{"type":"string"}},"required":["path","new_content","explanation"]}"""));
        }
        return defs;
    }

    private const string EmptySchema = """{"type":"object","properties":{}}""";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public bool IsLocal(string name) => name is RecordFinding or GetResultPage or ListProjectFiles or ReadProjectFile or ProposeChange;

    public async Task<ToolOutput> ExecuteAsync(string name, string inputJson, CancellationToken ct)
    {
        JsonNode? args;
        try { args = string.IsNullOrWhiteSpace(inputJson) ? new JsonObject() : JsonNode.Parse(inputJson); }
        catch (JsonException) { return new ToolOutput("arguments are not valid JSON", true); }
        if (args is not JsonObject obj) return new ToolOutput("arguments must be a JSON object", true);

        switch (name)
        {
            case RecordFinding: return Record(obj);
            case GetResultPage:
                return new ToolOutput(ResultShrinker.Page(_store, Str(obj, "result_id") ?? "", obj["page"]?.GetValue<int>() ?? 1), false);
            case ListProjectFiles when _ownRoot is not null:
                return new ToolOutput(string.Join('\n', CodeChange.ListFiles(_ownRoot)), false);
            case ReadProjectFile when _ownRoot is not null: return ReadOwn(obj);
            case ProposeChange when _ownRoot is not null: return Propose(obj);
        }

        if (_reaTools.All(t => t.Name != name)) return new ToolOutput($"tool \"{name}\" is not available for this project", true);
        var blocked = GuardPaths(obj);
        if (blocked is not null) return new ToolOutput(blocked, true);

        var r = await _rea.CallAsync(name, obj, _reaTimeout, ct).ConfigureAwait(false);
        return new ToolOutput(ResultShrinker.Shrink(r.Text, _store), r.IsError);
    }

    /// <summary>Model-supplied path arguments must stay inside the opened project (stops a hostile binary steering the model elsewhere).</summary>
    private string? GuardPaths(JsonObject obj)
    {
        if (_allowedRoots.Length == 0) return null;
        foreach (var kv in obj)
        {
            if (!PathKey.IsMatch(kv.Key)) continue;
            IEnumerable<string> values = kv.Value switch
            {
                JsonValue v when v.TryGetValue<string>(out var one) => new[] { one },
                JsonArray arr => arr.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var e) ? e : "").Where(x => x.Length > 0).ToList(),
                _ => Array.Empty<string>(),
            };
            foreach (var s in values)
            {
                var bad = CheckPath(kv.Key, s);
                if (bad is not null) return bad;
            }
        }
        return null;
    }

    private string? CheckPath(string key, string s)
    {
        if (!Path.IsPathRooted(s))
        {
            // A relative path is resolved by REA against its own working folder, so it must not climb out with "..".
            return s.Replace('\\', '/').Split('/').Any(p => p == "..") ? $"path \"{s}\" must not contain \"..\"" : null;
        }
        string full;
        try { full = Path.GetFullPath(s); } catch (ArgumentException) { return $"invalid path in \"{key}\""; }
        var ok = _allowedRoots.Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return ok ? null : $"path \"{s}\" is outside the project being analysed; only the opened project may be read";
    }

    private ToolOutput Record(JsonObject o)
    {
        var kind = Str(o, "kind");
        var text = Str(o, "text");
        if (string.IsNullOrWhiteSpace(text)) return new ToolOutput("text is required", true);
        var steps = (o["steps"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>();
        Finding f;
        switch (kind)
        {
            case "path": f = new Finding(FindingKind.Path, EvidenceLevel.Inferred, text, null, steps); break;
            case "evidence":
                var seen = Str(o, "level") == "seen";
                f = new Finding(FindingKind.Evidence, seen ? EvidenceLevel.Seen : EvidenceLevel.Inferred, text, Str(o, "location"), steps);
                break;
            case "unknown": f = new Finding(FindingKind.Unknown, EvidenceLevel.Unknown, text, null, steps); break;
            default: return new ToolOutput("kind must be path, evidence or unknown", true);
        }
        _emit(new FindingRecorded(f));
        return new ToolOutput("recorded", false);
    }

    private ToolOutput ReadOwn(JsonObject o)
    {
        var full = CodeChange.Resolve(_ownRoot!, Str(o, "path") ?? "");
        if (full is null || !File.Exists(full)) return new ToolOutput("file not found inside the project folder", true);
        if (new FileInfo(full).Length > CodeChange.MaxFileBytes) return new ToolOutput("file is too large to read", true);
        var text = File.ReadAllText(full);
        return new ToolOutput(text.Length > 40_000 ? text[..40_000] + "\n[ตัดที่ 40,000 อักขระ]" : text, false);
    }

    private ToolOutput Propose(JsonObject o)
    {
        var p = CodeChange.Propose(_ownRoot!, Str(o, "path") ?? "", Str(o, "new_content") ?? "", Str(o, "explanation") ?? "", out var err);
        if (err is not null) return new ToolOutput(err, true);
        _emit(new ProposalReady(p));
        return new ToolOutput("proposal shown to the user as a diff; nothing was written. The user must confirm before the file changes.", false);
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
