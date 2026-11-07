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
/// Everything the model can call: the selected REA tools plus local tools (record_finding, get_result_page, for a .NET target
/// list_dotnet_members/decompile_dotnet and, when the user chose their own project folder, list/read files and propose_file_change).
/// Big results are shrunk before they go back to the model.
/// </summary>
public sealed class AgentToolbox
{
    public const string RecordFinding = "record_finding";
    public const string GetResultPage = "get_result_page";
    public const string ListProjectFiles = "list_project_files";
    public const string ReadProjectFile = "read_project_file";
    public const string ProposeChange = "propose_file_change";
    public const string ListDotNetMembers = "list_dotnet_members";
    public const string DecompileDotNet = "decompile_dotnet";

    private static readonly Regex PathKey = new(@"(^|_)(path|file|artifact|binary|source|input|asar)s?$", RegexOptions.IgnoreCase);
    private static readonly Regex LocalPathKey = new(@"(^|_)(path|file|artifact|binary|source|input|asar|assembly|module|dll)s?$", RegexOptions.IgnoreCase);

    private readonly IReaToolCaller _rea;
    private readonly IReadOnlyList<ReaTool> _reaTools;
    private readonly string[] _allowedRoots;
    private readonly string? _ownRoot;
    private readonly DotNetCodeTools? _dotnet;
    private readonly ResultStore _store = new();
    private readonly Action<AgentEvent> _emit;
    private readonly TimeSpan _reaTimeout;

    /// <param name="targetKind">A <see cref="FileKind.DotNet"/> target also gets the local .NET listing and C# decompile tools.</param>
    /// <param name="dotnetCode">The caller's instance for this same target, kept across questions so the decompiler is built once.</param>
    public AgentToolbox(IReaToolCaller rea, IReadOnlyList<ReaTool> reaTools, string? targetPath, string? ownProjectRoot,
        Action<AgentEvent> emit, TimeSpan? reaTimeout = null, FileKind targetKind = FileKind.Unknown, DotNetCodeTools? dotnetCode = null)
    {
        _rea = rea;
        _reaTools = reaTools;
        _ownRoot = string.IsNullOrWhiteSpace(ownProjectRoot) ? null : ownProjectRoot;
        _emit = emit;
        _reaTimeout = reaTimeout ?? TimeSpan.FromMinutes(5);
        _allowedRoots = Roots(targetPath);
        if (targetKind == FileKind.DotNet && !string.IsNullOrWhiteSpace(targetPath))
            _dotnet = dotnetCode is not null && dotnetCode.Path.Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)
                ? dotnetCode
                : new DotNetCodeTools(targetPath);
    }

    /// <summary>The local .NET tools are offered (the target is a .NET assembly).</summary>
    public bool HasDotNetCode => _dotnet is not null;

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
        if (_dotnet is not null)
        {
            defs.Add(new AiToolDef(ListDotNetMembers,
                "List the types and methods of the opened .NET assembly, read locally from its metadata (works even when REA refuses the members). " +
                "Each line starts with a metadata token: 0x02… = type, 0x06… = method; pass it to decompile_dotnet. " +
                "filter = case-insensitive text matched against type full names and method names (a matching type is listed with all its methods). " +
                "types_only = true leaves the methods out: a quick map of a big assembly. Long lists are shortened; read more with get_result_page. " +
                "Always reads the opened file; takes no path.",
                """{"type":"object","properties":{"filter":{"type":"string","description":"part of a namespace, type or method name"},"types_only":{"type":"boolean"}}}"""));
            defs.Add(new AiToolDef(DecompileDotNet,
                "Decompile part of the opened .NET assembly to C#, locally (ILSpy engine; nothing in the target runs). Prefer this for reading .NET code. " +
                "Give token (0x06… method or 0x02… type, from list_dotnet_members), or type_name = full type name as listed (Namespace.Outer+Inner) " +
                "and optionally method_name = a method of that type (every overload with that name is returned). " +
                "whole_type = true with a method returns its whole declaring type. Property/event accessors come back as the property/event. " +
                "Long code is shortened; read more with get_result_page. Always reads the opened file; takes no path.",
                """{"type":"object","properties":{"token":{"type":"string","description":"0x06… method or 0x02… type token"},"type_name":{"type":"string"},"method_name":{"type":"string"},"whole_type":{"type":"boolean"}}}"""));
        }
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

    public bool IsLocal(string name) =>
        name is RecordFinding or GetResultPage or ListProjectFiles or ReadProjectFile or ProposeChange or ListDotNetMembers or DecompileDotNet;

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
            case ListDotNetMembers or DecompileDotNet when _dotnet is not null:
                return await DotNetAsync(_dotnet, name, obj, ct).ConfigureAwait(false);
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

    private async Task<ToolOutput> DotNetAsync(DotNetCodeTools dn, string name, JsonObject o, CancellationToken ct)
    {
        var foreign = ForeignPath(dn, name, o);
        if (foreign is not null) return new ToolOutput(foreign, true);
        ct.ThrowIfCancellationRequested();
        var work = name == ListDotNetMembers
            ? Task.Run(() => dn.List(Str(o, "filter"), Bool(o, "types_only"), ct), ct)
            : Task.Run(() => dn.Decompile(TokenArg(o), Str(o, "type_name"), Str(o, "method_name"), Bool(o, "whole_type"), ct), ct);
        // building the type system cannot be interrupted: a cancel returns at once and lets that finish (and be kept) in the background
        var r = await work.WaitAsync(ct).ConfigureAwait(false);
        return r.IsError ? r : new ToolOutput(ResultShrinker.Shrink(r.Text, _store), false);
    }

    /// <summary>
    /// The .NET tools read only the opened file. A path-like argument is refused unless it names that same file (models echo the
    /// target path from the system prompt), so a hostile binary cannot steer the decompiler at another file.
    /// </summary>
    private static string? ForeignPath(DotNetCodeTools dn, string tool, JsonObject o)
    {
        foreach (var kv in o)
        {
            if (!LocalPathKey.IsMatch(kv.Key) || kv.Value is null) continue;
            IEnumerable<JsonNode?> values = kv.Value is JsonArray arr ? arr : new[] { kv.Value };
            foreach (var v in values)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && IsTarget(dn, s)) continue;
                return $"{tool} reads only the opened assembly ({dn.FileName}) and takes no path or file; call again without \"{kv.Key}\"";
            }
        }
        return null;
    }

    private static bool IsTarget(DotNetCodeTools dn, string s)
    {
        if (s.Trim().Equals(dn.FileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.IsPathFullyQualified(s)) return false;
        try { return Path.GetFullPath(s).Equals(dn.Path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>A token as a string ("0x06000012"), or as the plain number some models send.</summary>
    private static string? TokenArg(JsonObject o) =>
        o["token"] is JsonValue v && v.TryGetValue<long>(out var n) && n is > 0 and <= int.MaxValue
            ? "0x" + n.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            : Str(o, "token");

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v
        && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p));

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
