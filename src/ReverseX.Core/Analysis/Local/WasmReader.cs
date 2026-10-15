using System.Text;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Parses a WebAssembly binary module: sections, imports/exports, memory, the name and producers custom sections.</summary>
public static class WasmReader
{
    private const int NameCap = 5000;
    private const int StringCap = 2000;

    private static readonly string[] SectionNames =
        { "custom", "type", "import", "function", "table", "memory", "global", "export", "start", "element", "code", "data", "datacount", "tag" };

    public static WasmModel Read(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new AnalysisProgress("wasm.parse", 0, 2));
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || bytes[0] != 0 || bytes[1] != 'a' || bytes[2] != 's' || bytes[3] != 'm')
            throw new InvalidDataException("not a WebAssembly module");
        var version = BitConverter.ToUInt32(bytes, 4);
        if (version != 1) throw new InvalidDataException($"WebAssembly version {version} is not supported");

        var r = new Reader(bytes, 8);
        var imports = new List<WasmImport>();
        var exports = new List<WasmExport>();
        var sections = new Dictionary<string, long>(StringComparer.Ordinal);
        var producers = new List<string>();
        var funcNames = new List<string>();
        var strings = new List<string>();
        int types = 0, defined = 0, tables = 0, globals = 0, data = 0;
        bool hasStart = false, shared = false;
        ulong? memMin = null, memMax = null;

        while (r.Pos < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            var id = r.Byte();
            var size = (int)r.U32();
            var start = r.Pos;
            var end = checked(start + size);
            if (end > bytes.Length) throw new InvalidDataException("section runs past the end of the file");
            var name = id < SectionNames.Length ? SectionNames[id] : "id" + id;
            var s = new Reader(bytes, start, end);
            try
            {
                switch (id)
                {
                    case 0:
                        var cname = s.Name();
                        name = "custom:" + cname;
                        if (cname == "name") ReadNames(s, funcNames);
                        else if (cname == "producers") ReadProducers(s, producers);
                        break;
                    case 1: types = (int)s.U32(); break;
                    case 2:
                        for (uint i = 0, n = s.U32(); i < n; i++)
                        {
                            var mod = s.Name(); var field = s.Name(); var kind = s.Byte();
                            switch (kind)
                            {
                                case 0: s.U32(); break;
                                case 1: s.Byte(); Limits(s, out _, out _, out _); break;
                                case 2: Limits(s, out var mn, out var mx, out var sh); memMin = mn; memMax = mx; shared |= sh; break;
                                case 3: s.Byte(); s.Byte(); break;
                                case 4: s.Byte(); s.U32(); break;
                                default: throw new InvalidDataException("bad import kind");
                            }
                            imports.Add(new WasmImport(mod, field, KindName(kind)));
                        }
                        break;
                    case 3: defined = (int)s.U32(); break;
                    case 4: tables = (int)s.U32(); break;
                    case 5:
                        if (s.U32() > 0) { Limits(s, out var mn, out var mx, out var sh); memMin = mn; memMax = mx; shared |= sh; }
                        break;
                    case 6: globals = (int)s.U32(); break;
                    case 7:
                        for (uint i = 0, n = s.U32(); i < n; i++)
                        {
                            var en = s.Name(); var kind = s.Byte(); var idx = s.U32();
                            exports.Add(new WasmExport(en, KindName(kind), idx));
                        }
                        break;
                    case 8: hasStart = true; break;
                    case 11:
                        data = (int)s.U32();
                        ReadDataStrings(bytes, start, end, strings);
                        break;
                }
            }
            catch (InvalidDataException) { }
            catch (IndexOutOfRangeException) { }

            sections[name] = sections.GetValueOrDefault(name) + size;
            r.Pos = end;
        }

        progress?.Report(new AnalysisProgress("wasm.scan", 1, 2));
        var model = new WasmModel
        {
            Version = version,
            Types = types,
            DefinedFunctions = defined,
            ImportedFunctions = imports.Count(i => i.Kind == "func"),
            Tables = tables,
            Globals = globals,
            DataSegments = data,
            HasStart = hasStart,
            MemoryMinPages = memMin,
            MemoryMaxPages = memMax,
            MemoryShared = shared,
            Imports = imports,
            Exports = exports,
            Sections = sections.Select(kv => new NamedCount(kv.Key, kv.Value)).ToList(),
            Producers = producers,
            FunctionNames = funcNames,
            Strings = strings,
        };
        model = model with { Toolchain = Toolchain(model) };
        progress?.Report(new AnalysisProgress("wasm.scan", 2, 2));
        return model with { Insights = Insights(model) };
    }

    public static string Toolchain(WasmModel m)
    {
        var mods = m.Imports.Select(i => i.Module).ToHashSet(StringComparer.Ordinal);
        var prod = string.Join(" ", m.Producers);
        if (mods.Contains("__wbindgen_placeholder__") || m.Imports.Any(i => i.Name.StartsWith("__wbg_", StringComparison.Ordinal)) || prod.Contains("rustc"))
            return m.Imports.Any(i => i.Name.StartsWith("__wbg_", StringComparison.Ordinal)) ? "Rust (wasm-bindgen)" : "Rust";
        if (mods.Contains("go") || mods.Contains("gojs") || m.Imports.Any(i => i.Name.StartsWith("runtime.", StringComparison.Ordinal) || i.Name.StartsWith("syscall/js.", StringComparison.Ordinal)))
            return "Go";
        if (prod.Contains("TinyGo", StringComparison.OrdinalIgnoreCase)) return "TinyGo";
        if (prod.Contains("Emscripten", StringComparison.OrdinalIgnoreCase) || m.Imports.Any(i => i.Name is "emscripten_memcpy_big" or "emscripten_resize_heap" or "_emscripten_memcpy_js")
            || m.Exports.Any(e => e.Name is "_initialize" or "__wasm_call_ctors" or "stackSave") && mods.Contains("env") && m.Imports.Any(i => i.Name.StartsWith("emscripten_", StringComparison.Ordinal)))
            return "C/C++ (Emscripten)";
        if (prod.Contains("AssemblyScript", StringComparison.OrdinalIgnoreCase) || m.Imports.Any(i => i.Module == "env" && i.Name == "abort") && m.Exports.Any(e => e.Name == "__new"))
            return "AssemblyScript";
        if (m.FunctionNames.Any(n => n.StartsWith("mono_", StringComparison.Ordinal)) || m.Imports.Any(i => i.Name.StartsWith("mono_", StringComparison.Ordinal)))
            return ".NET (Blazor/Mono)";
        if (mods.Any(x => x.StartsWith("wasi_", StringComparison.Ordinal) || x.StartsWith("wasi:", StringComparison.Ordinal))) return "WASI";
        if (prod.Contains("clang", StringComparison.OrdinalIgnoreCase)) return "C/C++ (clang)";
        return "";
    }

    private static List<Insight> Insights(WasmModel m)
    {
        var list = new List<Insight>();
        if (m.Toolchain.Length > 0) list.Add(Insight.Of("WasmToolchain", InsightTone.Info, m.Producers.Count > 0 ? EvidenceLevel.Seen : EvidenceLevel.Inferred, m.Toolchain));
        if (m.FunctionNames.Count > 0) list.Add(Insight.Of("WasmHasNames", InsightTone.Good, EvidenceLevel.Seen, m.FunctionNames.Count));
        else list.Add(Insight.Of("WasmStripped", InsightTone.Info, EvidenceLevel.Seen));
        if (m.MemoryShared) list.Add(Insight.Of("WasmSharedMemory", InsightTone.Info, EvidenceLevel.Seen));
        if (m.HasStart) list.Add(Insight.Of("WasmStart", InsightTone.Info, EvidenceLevel.Seen));
        var wasi = m.Imports.Where(i => i.Module.StartsWith("wasi", StringComparison.Ordinal)).Select(i => i.Name).ToList();
        if (wasi.Any(n => n.StartsWith("sock_", StringComparison.Ordinal))) list.Add(Insight.Of("WasmSockets", InsightTone.Warn, EvidenceLevel.Seen));
        if (wasi.Any(n => n.StartsWith("path_", StringComparison.Ordinal) || n.StartsWith("fd_", StringComparison.Ordinal) && n != "fd_write"))
            list.Add(Insight.Of("WasmFilesystem", InsightTone.Info, EvidenceLevel.Seen));
        if (m.Imports.Any(i => i.Name.Contains("eval", StringComparison.OrdinalIgnoreCase)))
            list.Add(Insight.Of("WasmJsEval", InsightTone.Warn, EvidenceLevel.Seen));
        if (m.MemoryMinPages is { } p && p * 65536 >= 256UL * 1024 * 1024)
            list.Add(Insight.Of("WasmBigMemory", InsightTone.Info, EvidenceLevel.Seen, p * 64 / 1024));
        return list;
    }

    private static void ReadNames(Reader s, List<string> names)
    {
        while (s.Pos < s.End)
        {
            var sub = s.Byte();
            var size = (int)s.U32();
            var end = s.Pos + size;
            if (sub == 1)
            {
                var n = s.U32();
                for (uint i = 0; i < n && names.Count < NameCap; i++) { s.U32(); names.Add(s.Name()); }
            }
            s.Pos = end;
        }
    }

    private static void ReadProducers(Reader s, List<string> producers)
    {
        for (uint i = 0, n = s.U32(); i < n; i++)
        {
            var field = s.Name();
            for (uint j = 0, m = s.U32(); j < m; j++)
            {
                var name = s.Name(); var ver = s.Name();
                producers.Add($"{field}: {name}{(ver.Length > 0 ? " " + ver : "")}");
            }
        }
    }

    // Printable ASCII runs inside data segments: error messages, URLs, format strings.
    private static void ReadDataStrings(byte[] b, int start, int end, List<string> strings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var run = start;
        for (var i = start; i <= end && strings.Count < StringCap; i++)
        {
            var printable = i < end && b[i] is >= 0x20 and < 0x7f or (byte)'\t';
            if (printable) continue;
            if (i - run >= 6)
            {
                var s = Encoding.ASCII.GetString(b, run, Math.Min(i - run, 300));
                if (s.Any(char.IsLetter) && seen.Add(s)) strings.Add(s);
            }
            run = i + 1;
        }
    }

    private static void Limits(Reader s, out ulong min, out ulong? max, out bool shared)
    {
        var flags = s.Byte();
        var is64 = (flags & 4) != 0;
        shared = (flags & 2) != 0;
        min = is64 ? s.U64() : s.U32();
        max = (flags & 1) != 0 ? (is64 ? s.U64() : s.U32()) : null;
    }

    private static string KindName(byte k) => k switch { 0 => "func", 1 => "table", 2 => "memory", 3 => "global", 4 => "tag", _ => "?" };

    private sealed class Reader
    {
        private readonly byte[] _b;
        public int Pos;
        public readonly int End;
        public Reader(byte[] b, int pos, int end = -1) { _b = b; Pos = pos; End = end < 0 ? b.Length : end; }

        public byte Byte() => Pos < End ? _b[Pos++] : throw new InvalidDataException("unexpected end of section");

        public uint U32() => (uint)U64();

        public ulong U64()
        {
            ulong result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var x = Byte();
                result |= (ulong)(x & 0x7f) << shift;
                if ((x & 0x80) == 0) return result;
            }
            throw new InvalidDataException("bad LEB128");
        }

        public string Name()
        {
            var n = (int)U32();
            if (n < 0 || Pos + n > End) throw new InvalidDataException("name runs past section");
            var s = Encoding.UTF8.GetString(_b, Pos, n);
            Pos += n;
            return s;
        }
    }
}
