using System.Globalization;
using System.Text.Json;

namespace ReverseX.Core.Analysis;

public sealed record ReaErrorInfo(string Code, string Category, string Message, List<EngineRejection> Rejections);

/// <summary>Turns the JSON text REA returns (MCP tool text or CLI envelope) into our own models. Lenient: missing fields become defaults.</summary>
public static class ReaResultParser
{
    // ───────────── helpers ─────────────

    /// <summary>The MCP tool text is the Evidence record itself; the CLI wraps it as { ok, data, meta }.</summary>
    internal static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)
            && d.ValueKind == JsonValueKind.Object && !root.TryGetProperty("normalized_result", out _))
            return d;
        return root;
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int Int(JsonElement e, string name) => IntOrNull(e, name) ?? 0;

    private static int? IntOrNull(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static long Long(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static List<string> Strings(JsonElement e, string name) =>
        Items(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();

    private static Dictionary<string, int> IntMap(JsonElement e)
    {
        var map = new Dictionary<string, int>();
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) map[p.Name] = n;
        return map;
    }

    // ───────────── errors ─────────────

    /// <summary>Returns the error REA reported, or null if the text is not an error.</summary>
    public static ReaErrorInfo? ParseError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.TrimStart();
        if (t.StartsWith("Input validation error", StringComparison.Ordinal))
            return new ReaErrorInfo("invalid_input", "invalid_input", text.Trim(), new());
        if (t[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var err = Prop(doc.RootElement, "error");
            if (err.ValueKind != JsonValueKind.Object) return null;
            var rej = Items(Prop(err, "details"), "rejections")
                .Select(r => new EngineRejection(Str(r, "provider_id") ?? "", Str(r, "code") ?? "", Str(r, "reason") ?? ""))
                .ToList();
            return new ReaErrorInfo(Str(err, "code") ?? "unknown", Str(err, "category") ?? "", Str(err, "message") ?? "", rej);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ───────────── .NET ─────────────

    public static DotNetModel ParseManaged(string artifactJson, string membersJson)
    {
        using var aDoc = JsonDocument.Parse(artifactJson);
        using var mDoc = JsonDocument.Parse(membersJson);
        var aEv = Unwrap(aDoc.RootElement);
        var a = Prop(aEv, "normalized_result");
        var m = Prop(Unwrap(mDoc.RootElement), "normalized_result");

        var artifact = Prop(a, "artifact");
        var rowCounts = IntMap(Prop(Prop(a, "metadata"), "table_row_counts"));

        var info = new DotNetAssemblyInfo
        {
            Name = Str(Prop(a, "assembly"), "name") ?? Str(Prop(a, "module"), "name") ?? "",
            Version = FormatVersion(Prop(a, "assembly")),
            TargetFramework = Items(a, "target_frameworks").Select(TargetFrameworkName).FirstOrDefault(s => s is not null),
            RuntimeFamily = Str(Prop(a, "classification"), "runtime_family") ?? Str(Prop(a, "classification"), "kind"),
            Architecture = Str(Prop(a, "pe"), "architecture"),
            Sha256 = Str(artifact, "sha256") ?? "",
            ByteLength = Long(artifact, "byte_length"),
            Mvid = Str(Prop(a, "module"), "mvid"),
            MetadataVersion = Str(Prop(a, "metadata"), "version"),
            CoverageState = Str(Prop(a, "coverage"), "state") ?? Str(Prop(a, "coverage"), "status"),
            Confidence = Str(aEv, "confidence") ?? "observed",
            TableRowCounts = rowCounts,
            References = Items(a, "references").Select(r => new DotNetAssemblyRef(Str(r, "name") ?? "", FormatVersion(r))).ToList(),
            ResourceCount = Items(a, "resources").Count(),
            AttributeCount = rowCounts.GetValueOrDefault("CustomAttribute"),
            Limitations = Strings(a, "limitations"),
        };

        // lookup tables for resolving tokens to readable names
        var methodNames = new Dictionary<string, string>();
        foreach (var x in Items(m, "methods"))
            methodNames[Str(x, "token") ?? ""] = ShortType(Str(x, "declaring_type")) + "::" + (Str(x, "name") ?? "?");
        var fieldNames = new Dictionary<string, string>();
        foreach (var f in Items(m, "fields"))
            fieldNames[Str(f, "token") ?? ""] = (Str(f, "name") ?? "?");
        var memberRefs = new Dictionary<string, string>();
        foreach (var r in Items(m, "member_refs"))
            memberRefs[Str(r, "token") ?? ""] = Str(r, "name") ?? "?";

        var callsByMethod = new Dictionary<string, List<DotNetCall>>();
        foreach (var c in Items(m, "call_edges"))
        {
            var caller = Str(c, "caller_token") ?? "";
            var token = Str(c, "target_token");
            var kind = Str(c, "target_kind") ?? "";
            var name = Str(c, "target_name") ?? "?";
            if (token is not null && methodNames.TryGetValue(token, out var full)) name = full;
            if (!callsByMethod.TryGetValue(caller, out var list)) callsByMethod[caller] = list = new();
            var call = new DotNetCall(name, kind, token, Str(c, "opcode") ?? "call");
            if (!list.Contains(call)) list.Add(call);
        }
        var fieldsByMethod = new Dictionary<string, List<string>>();
        foreach (var fa in Items(m, "field_accesses"))
        {
            var mt = Str(fa, "method_token") ?? "";
            if (!fieldsByMethod.TryGetValue(mt, out var list)) fieldsByMethod[mt] = list = new();
            var text = (Str(fa, "opcode") ?? "ldfld") + " " + (Str(fa, "field_name") ?? Str(fa, "name") ?? "?");
            if (!list.Contains(text)) list.Add(text);
        }

        var methodsByType = new Dictionary<string, List<DotNetMethod>>();
        foreach (var x in Items(m, "methods"))
        {
            var token = Str(x, "token") ?? "";
            var body = Prop(x, "body");
            var anchors = Items(body, "anchors").Select(an =>
            {
                var kind = Str(an, "operand_kind") ?? "";
                return new DotNetAnchor(Int(an, "il_offset"), Str(an, "opcode") ?? "", kind,
                    ResolveOperand(kind, Str(an, "operand") ?? "", methodNames, memberRefs, fieldNames));
            }).ToList();
            var method = new DotNetMethod
            {
                Token = token,
                Name = Str(x, "name") ?? "?",
                DeclaringType = Str(x, "declaring_type") ?? "",
                Signature = FormatSignature(Prop(x, "signature")),
                Flags = Int(x, "flags"),
                HasBody = Str(body, "status") == "present",
                IlSize = IntOrNull(body, "il_size"),
                InstructionCount = IntOrNull(body, "instruction_count"),
                MaxStack = IntOrNull(body, "max_stack"),
                IlSha256 = Str(body, "il_sha256"),
                OpcodeCounts = IntMap(Prop(body, "opcode_counts")),
                Anchors = anchors,
                Calls = callsByMethod.GetValueOrDefault(token) ?? new(),
                FieldAccesses = fieldsByMethod.GetValueOrDefault(token) ?? new(),
            };
            var owner = Str(x, "declaring_type_token") ?? "";
            if (!methodsByType.TryGetValue(owner, out var ml)) methodsByType[owner] = ml = new();
            ml.Add(method);
        }

        var types = Items(m, "types").Select(t =>
        {
            var token = Str(t, "token") ?? "";
            return new DotNetType
            {
                Token = token,
                Namespace = Str(t, "namespace") ?? "",
                Name = Str(t, "name") ?? "",
                FullName = Str(t, "full_name") ?? "",
                FieldCount = Int(Prop(t, "field_list"), "count"),
                Methods = methodsByType.GetValueOrDefault(token) ?? new(),
            };
        }).ToList();

        return new DotNetModel
        {
            Info = info,
            Types = types,
            MemberRefCount = Items(m, "member_refs").Count(),
            MembersCoverage = Str(Prop(m, "coverage"), "state") ?? Str(Prop(m, "coverage"), "status"),
            MembersLimitations = Strings(m, "limitations"),
        };
    }

    private static string? TargetFrameworkName(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        return Str(e, "framework_name") ?? Str(e, "name") ?? Str(e, "value");
    }

    private static string FormatVersion(JsonElement e)
    {
        var s = Str(e, "version");
        if (s is not null) return s;
        var v = Prop(e, "version");
        if (v.ValueKind == JsonValueKind.Object)
            return string.Join('.', new[] { "major", "minor", "build", "revision" }.Select(k => Int(v, k).ToString(CultureInfo.InvariantCulture)));
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("major", out _))
            return string.Join('.', new[] { "major", "minor", "build", "revision" }.Select(k => Int(e, k).ToString(CultureInfo.InvariantCulture)));
        return "";
    }

    private static string ShortType(string? full)
    {
        if (string.IsNullOrEmpty(full)) return "";
        var i = full.LastIndexOf('.');
        return i >= 0 ? full[(i + 1)..] : full;
    }

    private static string FormatSignature(JsonElement sig)
    {
        if (sig.ValueKind != JsonValueKind.Object) return "()";
        if (Str(sig, "kind") == "field") return Str(sig, "field_type") ?? "?";
        return $"({string.Join(", ", Strings(sig, "parameter_types"))}) → {Str(sig, "return_type") ?? "?"}";
    }

    private static string ResolveOperand(string kind, string operand, Dictionary<string, string> methods,
        Dictionary<string, string> memberRefs, Dictionary<string, string> fields)
    {
        switch (kind)
        {
            case "method":
                if (methods.TryGetValue(operand, out var mn)) return mn;
                return memberRefs.TryGetValue(operand, out var rn) ? "::" + rn : operand;
            case "field":
                if (fields.TryGetValue(operand, out var fn)) return fn;
                return memberRefs.TryGetValue(operand, out var fr) ? "::" + fr : operand;
            case "branch":
                return int.TryParse(operand, NumberStyles.Integer, CultureInfo.InvariantCulture, out var off)
                    ? "IL_" + off.ToString("X4", CultureInfo.InvariantCulture)
                    : operand;
            case "string":
                return "string " + operand; // REA gives the #US heap token, not the text
            default:
                return operand;
        }
    }

    // ───────────── JS / Electron ─────────────

    public static JsAppSummary ParseJsSummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ev = Unwrap(doc.RootElement);
        var n = Prop(ev, "normalized_result");
        var s = Prop(n, "summary");
        var stats = Prop(s, "statistics");
        var el = Prop(s, "electron");
        var ipc = Prop(el, "ipc");
        var cov = Prop(s, "coverage");
        var limits = Strings(n, "limitations");
        // REA repeats every limitation inside unknowns; keep only the extra lines.
        var unknowns = Strings(n, "unknowns").Where(u => !limits.Contains(u)).ToList();
        return new JsAppSummary
        {
            InputPath = Str(s, "input_path") ?? "",
            Format = Str(s, "format") ?? "",
            Sha256 = Str(s, "root_artifact_sha256") ?? "",
            Confidence = Str(ev, "confidence") ?? "observed",
            RelevantFiles = Int(stats, "relevant_files"),
            ParsedFiles = Int(stats, "parsed_javascript_files"),
            Findings = Int(stats, "findings"),
            ParseFailures = Int(stats, "parse_failures"),
            BrowserWindows = Int(el, "browser_windows"),
            PreloadEntrypoints = Int(el, "preload_entrypoints"),
            ContextBridgeApis = Int(el, "context_bridge_apis"),
            ExposedApiMembers = Int(el, "exposed_api_members"),
            IpcOperations = Int(ipc, "operations"),
            IpcLiteralChannels = Int(ipc, "literal_channels"),
            IpcDynamicOperations = Int(ipc, "dynamic_channel_operations"),
            RendererTransmissions = Int(ipc, "renderer_transmissions"),
            RendererListeners = Int(ipc, "renderer_listeners"),
            MainHandlers = Int(ipc, "main_handlers"),
            PairedTransmissions = Int(ipc, "paired_renderer_transmissions"),
            UnpairedLiteralTransmissions = Int(ipc, "unpaired_literal_renderer_transmissions"),
            NativeAddonBindings = Int(el, "native_addon_bindings"),
            ResolvedNativeAddons = Int(el, "resolved_native_addon_bindings"),
            UtilityProcesses = Int(el, "utility_processes"),
            ApplicationCoverage = Str(Prop(cov, "application"), "status"),
            SemanticCoverage = Str(Prop(cov, "semantic"), "status"),
            SemanticUnknowns = Int(cov, "semantic_unknowns"),
            ParentEvidenceId = Str(n, "parent_evidence_id"),
            IntegrityMismatch = limits.Any(l => l.Contains("integrity metadata contradicts", StringComparison.OrdinalIgnoreCase)),
            Limitations = limits,
            Unknowns = unknowns,
        };
    }

    /// <summary>One page of the module list: items plus REA's own total and next offset (null on the last page).</summary>
    public static (List<JsModuleItem> Items, int Total, int? NextOffset) ParseJsModulesPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var n = Prop(Unwrap(doc.RootElement), "normalized_result");
        var items = Items(n, "items")
            .Select(i => new JsModuleItem(Str(i, "node_id") ?? "", Str(i, "kind") ?? "", Str(i, "path") ?? ""))
            .ToList();
        var cov = Prop(n, "coverage");
        return (items, Int(cov, "total"), IntOrNull(cov, "next_offset"));
    }

    // ───────────── native ─────────────

    public static NativeIdentity? ParseNativeIdentity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = Prop(doc.RootElement, "result");
        if (r.ValueKind != JsonValueKind.Object) return null;
        return new NativeIdentity(Str(r, "path") ?? "", Str(r, "format") ?? "", Str(r, "kind") ?? "", Str(r, "sha256") ?? "", Str(r, "architecture"));
    }

    /// <summary>list_procedures answers address/value pairs; find the first array of such objects wherever it sits.</summary>
    public static List<NativeProcedure> ParseProcedures(string json)
    {
        var list = new List<NativeProcedure>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = FindPairArray(doc.RootElement, 0);
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.Object)
                    list.Add(new NativeProcedure(Str(it, "address") ?? "", Str(it, "value") ?? Str(it, "name") ?? ""));
        }
        catch (JsonException) { }
        return list;
    }

    private static JsonElement FindPairArray(JsonElement e, int depth)
    {
        if (depth > 5) return default;
        if (e.ValueKind == JsonValueKind.Array)
        {
            var first = e.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("address", out _) ? e : default;
        }
        if (e.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in e.EnumerateObject())
        {
            var found = FindPairArray(p.Value, depth + 1);
            if (found.ValueKind == JsonValueKind.Array) return found;
        }
        return default;
    }
}
