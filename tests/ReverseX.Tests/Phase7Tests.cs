using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using ReverseX.Core.Analysis;
using ReverseX.Core.Analysis.Local;
using ReverseX.Core.Files;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Local readers (no REA): APK, SQLite, HAR, WASM, .NET metadata. Inputs are built in the test.</summary>
public sealed class Phase7Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-p7-" + Guid.NewGuid().ToString("N")[..8]);

    // Built at runtime so no credential-shaped literal sits in the source.
    private static readonly string FakeGoogleKey = "AI" + "za" + new string('Q', 35);

    public Phase7Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    // ───────────── binary builders ─────────────

    private sealed class Axml
    {
        private readonly List<string> _strings = new();
        private readonly MemoryStream _body = new();
        public int S(string s) { var i = _strings.IndexOf(s); if (i >= 0) return i; _strings.Add(s); return _strings.Count - 1; }

        private void Chunk(ushort type, ushort headerSize, byte[] payload)
        {
            var w = new BinaryWriter(_body);
            w.Write(type); w.Write(headerSize); w.Write(8 + payload.Length); w.Write(payload);
        }

        public void Ns(string prefix, string uri)
        {
            var p = new MemoryStream(); var w = new BinaryWriter(p);
            w.Write(1); w.Write(-1); w.Write(S(prefix)); w.Write(S(uri));
            Chunk(0x0100, 16, p.ToArray());
        }

        /// <param name="attrs">(namespace uri or null, name, type: 's' string / 'b' bool / 'i' int, value)</param>
        public void Start(string name, params (string? Ns, string Name, char Type, object Value)[] attrs)
        {
            var p = new MemoryStream(); var w = new BinaryWriter(p);
            w.Write(1); w.Write(-1);
            w.Write(-1); w.Write(S(name));
            w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)attrs.Length);
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
            foreach (var (ns, an, t, v) in attrs)
            {
                w.Write(ns is null ? -1 : S(ns)); w.Write(S(an));
                var raw = t == 's' ? S((string)v) : -1;
                w.Write(raw); w.Write((ushort)8); w.Write((byte)0);
                w.Write(t switch { 's' => (byte)0x03, 'b' => (byte)0x12, _ => (byte)0x10 });
                w.Write(t switch { 's' => raw, 'b' => (bool)v ? -1 : 0, _ => (int)v });
            }
            Chunk(0x0102, 16, p.ToArray());
        }

        public void End(string name)
        {
            var p = new MemoryStream(); var w = new BinaryWriter(p);
            w.Write(1); w.Write(-1); w.Write(-1); w.Write(S(name));
            Chunk(0x0103, 16, p.ToArray());
        }

        public byte[] Build()
        {
            var data = new MemoryStream(); var dw = new BinaryWriter(data);
            var offsets = new List<int>();
            foreach (var s in _strings)
            {
                offsets.Add((int)data.Length);
                dw.Write((ushort)s.Length); dw.Write(Encoding.Unicode.GetBytes(s)); dw.Write((ushort)0);
            }
            while (data.Length % 4 != 0) dw.Write((byte)0);
            var pool = new MemoryStream(); var pw = new BinaryWriter(pool);
            var poolSize = 28 + offsets.Count * 4 + (int)data.Length;
            pw.Write((ushort)1); pw.Write((ushort)28); pw.Write(poolSize);
            pw.Write(offsets.Count); pw.Write(0); pw.Write(0); pw.Write(28 + offsets.Count * 4); pw.Write(0);
            foreach (var o in offsets) pw.Write(o);
            pw.Write(data.ToArray());

            var all = new MemoryStream(); var aw = new BinaryWriter(all);
            aw.Write((ushort)3); aw.Write((ushort)8); aw.Write(8 + (int)pool.Length + (int)_body.Length);
            aw.Write(pool.ToArray()); aw.Write(_body.ToArray());
            return all.ToArray();
        }
    }

    private static byte[] Dex(string[] classDescriptors, params string[] extraStrings)
    {
        var strings = classDescriptors.Concat(extraStrings).ToList();
        int stringIds = 0x70, typeIds = stringIds + strings.Count * 4, classDefs = typeIds + classDescriptors.Length * 4;
        var dataStart = classDefs + classDescriptors.Length * 32;
        var data = new MemoryStream();
        var offs = new List<int>();
        foreach (var s in strings)
        {
            offs.Add(dataStart + (int)data.Length);
            data.WriteByte((byte)s.Length);
            data.Write(Encoding.UTF8.GetBytes(s));
            data.WriteByte(0);
        }
        var d = new byte[dataStart + data.Length];
        "dex\n035\0"u8.CopyTo(d);
        void U32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), v);
        U32(0x20, d.Length); U32(0x24, 0x70);
        U32(0x38, strings.Count); U32(0x3C, stringIds);
        U32(0x40, classDescriptors.Length); U32(0x44, typeIds);
        U32(0x60, classDescriptors.Length); U32(0x64, classDefs);
        for (var i = 0; i < strings.Count; i++) U32(stringIds + i * 4, offs[i]);
        for (var i = 0; i < classDescriptors.Length; i++) { U32(typeIds + i * 4, i); U32(classDefs + i * 32, i); }
        data.ToArray().CopyTo(d, dataStart);
        return d;
    }

    private static void WriteZip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var s = z.CreateEntry(name).Open();
            s.Write(bytes);
        }
    }

    private static byte[] Manifest()
    {
        const string android = "http://schemas.android.com/apk/res/android";
        var x = new Axml();
        x.Ns("android", android);
        x.Start("manifest", (null, "package", 's', "com.test.app"), (android, "versionName", 's', "1.2.3"));
        x.Start("uses-sdk", (android, "minSdkVersion", 'i', 21), (android, "targetSdkVersion", 'i', 28)); x.End("uses-sdk");
        x.Start("uses-permission", (android, "name", 's', "android.permission.CAMERA")); x.End("uses-permission");
        x.Start("uses-permission", (android, "name", 's', "android.permission.INTERNET")); x.End("uses-permission");
        x.Start("application", (android, "debuggable", 'b', true), (android, "allowBackup", 'b', false));
        x.Start("activity", (android, "name", 's', ".Main"), (android, "exported", 'b', true));
        x.Start("intent-filter");
        x.Start("action", (android, "name", 's', "android.intent.action.VIEW")); x.End("action");
        x.Start("data", (android, "scheme", 's', "testapp")); x.End("data");
        x.End("intent-filter");
        x.End("activity");
        x.End("application");
        x.End("manifest");
        return x.Build();
    }

    // ───────────── Android ─────────────

    [Fact]
    public void BinaryXml_DecodesElementsAndAndroidAttributes()
    {
        var doc = AndroidBinaryXml.Decode(Manifest());
        Assert.Equal("manifest", doc.Root!.Name.LocalName);
        Assert.Equal("com.test.app", doc.Root.Attribute("package")?.Value);
        var app = doc.Root.Element("application")!;
        Assert.Equal("true", app.Attribute(AndroidBinaryXml.AndroidNs + "debuggable")?.Value);
        Assert.Equal("28", doc.Root.Element("uses-sdk")!.Attribute(AndroidBinaryXml.AndroidNs + "targetSdkVersion")?.Value);
    }

    [Fact]
    public void BinaryXml_RejectsPlainXml() =>
        Assert.Throws<InvalidDataException>(() => AndroidBinaryXml.Decode("<manifest/>"u8.ToArray()));

    [Fact]
    public void Dex_ReadsClassNamesAndStrings()
    {
        var seen = new List<string>();
        var info = DexReader.Read(Dex(new[] { "Lcom/test/app/Main;", "Lokhttp3/OkHttpClient;" }, "https://api.test.example/v1"), seen.Add, default);
        Assert.Equal("035", info.Version);
        Assert.Equal(new[] { "com.test.app.Main", "okhttp3.OkHttpClient" }, info.Classes);
        Assert.Contains("https://api.test.example/v1", seen);
    }

    [Fact]
    public void Apk_ReadsManifestDexAndFlagsRisks()
    {
        var apk = PathOf("t.apk");
        WriteZip(apk,
            ("AndroidManifest.xml", Manifest()),
            ("classes.dex", Dex(new[] { "Lcom/test/app/Main;", "Lokhttp3/OkHttpClient;" }, "http://plain.test.example/api", "key=" + FakeGoogleKey)),
            ("lib/arm64-v8a/libflutter.so", new byte[16]));

        Assert.Equal(FileKind.Apk, FileKindDetector.Detect(apk).Kind);
        var m = ApkReader.Read(apk, null, default);

        Assert.Equal("com.test.app", m.PackageName);
        Assert.Equal("1.2.3", m.VersionName);
        Assert.Equal(28, m.TargetSdk);
        Assert.True(m.Debuggable);
        Assert.False(m.AllowBackup);
        Assert.Contains(m.Permissions, p => p.Name == "android.permission.CAMERA" && p.Protection == "dangerous");
        var main = Assert.Single(m.Components);
        Assert.Equal("com.test.app.Main", main.Name);
        Assert.True(main.Exported);
        Assert.Contains("testapp", m.Schemes);
        Assert.Equal(2, m.ClassCount);
        Assert.Contains("arm64-v8a", m.Abis);
        Assert.Contains(m.Urls, u => u.StartsWith("http://plain.test.example", StringComparison.Ordinal));
        Assert.Contains(m.Secrets, s => s.Kind == "Google API key" && !s.Masked.Contains(new string('Q', 20)));
        Assert.Contains(m.Insights, i => i.Code == "ApkDebuggable");
        Assert.Contains(m.Insights, i => i.Code == "ApkUnsigned");
        Assert.DoesNotContain(m.Insights, i => i.Code == "ApkBackup");
        Assert.Contains("<manifest", m.ManifestXml);
    }

    [Fact]
    public void Xapk_PicksBaseApkAndListsSplits()
    {
        var baseApk = PathOf("base.apk");
        WriteZip(baseApk, ("AndroidManifest.xml", Manifest()), ("classes.dex", Dex(new[] { "Lcom/test/app/Main;" })));
        var split = PathOf("split.apk");
        WriteZip(split, ("lib/x86_64/libgame.so", new byte[8]));
        var xapk = PathOf("t.xapk");
        WriteZip(xapk, ("manifest.json", "{}"u8.ToArray()), ("base.apk", File.ReadAllBytes(baseApk)), ("config.x86_64.apk", File.ReadAllBytes(split)));

        var kind = FileKindDetector.Detect(xapk);
        Assert.Equal(FileKind.Apk, kind.Kind);
        Assert.Equal("XAPK", kind.Detail);
        var m = ApkReader.Read(xapk, null, default);
        Assert.Equal("com.test.app", m.PackageName);
        Assert.Contains("config.x86_64.apk", m.Splits);
        Assert.Contains(m.NativeLibs, l => l.Abi == "x86_64" && l.Name == "libgame.so");
    }

    [Fact]
    public void Apk_Garbage_BecomesInputRejected()
    {
        var apk = PathOf("bad.apk");
        WriteZip(apk, ("AndroidManifest.xml", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        var ex = Assert.Throws<AnalysisFailure>(() => LocalAnalyzer.Analyze(apk, AnalysisKind.Android, null, default));
        Assert.Equal(AnalysisFailureKind.InputRejected, ex.Kind);
    }

    // ───────────── SQLite ─────────────

    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [Fact]
    public void Sqlite_ListsTablesRowsAndSensitiveColumns_WithoutChangingFile()
    {
        var db = PathOf("t.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE users(id INTEGER PRIMARY KEY, email TEXT NOT NULL, password_hash TEXT, avatar BLOB)");
            Exec(c, "CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), total REAL)");
            Exec(c, "CREATE INDEX ix_orders_user ON orders(user_id)");
            Exec(c, "INSERT INTO users(email, password_hash, avatar) VALUES ('a@x.test','h1', x'89504E470D0A1A0A0000'), ('b@x.test','h2',NULL), ('c@x.test','h3',NULL)");
        }
        var before = File.ReadAllBytes(db);

        var m = SqliteReader.Read(db, null, default);

        var users = Assert.Single(m.Tables, t => t.Name == "users");
        Assert.Equal(3, users.RowCount);
        Assert.Equal(4, users.Columns.Count);
        Assert.True(users.Columns.Single(c => c.Name == "id").PrimaryKey);
        Assert.Contains("password_hash", users.SensitiveColumns);
        Assert.Equal(3, users.Sample.Count);
        Assert.Contains(users.Sample[0], v => v != null && v.Contains("PNG", StringComparison.Ordinal));
        var orders = Assert.Single(m.Tables, t => t.Name == "orders");
        Assert.NotEmpty(orders.ForeignKeys);
        Assert.Contains("ix_orders_user", orders.Indexes);
        Assert.Equal(before, File.ReadAllBytes(db));
    }

    [Fact]
    public void Sqlite_IncludesUncheckpointedWalRows()
    {
        var db = PathOf("wal.db");
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL");
        Exec(c, "PRAGMA wal_autocheckpoint=0");
        Exec(c, "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT)");
        Exec(c, "INSERT INTO notes(body) VALUES ('one'), ('two')");
        Assert.True(new FileInfo(db + "-wal").Length > 0);

        var m = SqliteReader.Read(db, null, default);

        Assert.True(m.WalMode);
        Assert.True(m.WalIncluded);
        Assert.Equal(2, Assert.Single(m.Tables, t => t.Name == "notes").RowCount);
    }

    [Fact]
    public void Sqlite_NotADatabase_IsRejected()
    {
        var f = PathOf("x.db");
        File.WriteAllText(f, new string('x', 200));
        Assert.Equal(AnalysisFailureKind.InputRejected, Assert.Throws<AnalysisFailure>(() => LocalAnalyzer.Analyze(f, AnalysisKind.Db, null, default)).Kind);
    }

    // ───────────── HAR ─────────────

    private const string Har = """
    {"log":{"version":"1.2","creator":{"name":"WebInspector","version":"537.36"},"pages":[{"id":"p1"}],"entries":[
      {"startedDateTime":"2026-01-01T00:00:00.000Z","time":120,"serverIPAddress":"[2001:db8::1]",
       "request":{"method":"GET","url":"https://api.shop.test/v1/items/12345?token=SECRET123&lang=th","headers":[{"name":"Authorization","value":"Bearer abc.def.ghi"},{"name":"Accept","value":"*/*"}],"queryString":[{"name":"token","value":"SECRET123"},{"name":"lang","value":"th"}],"headersSize":100,"bodySize":0},
       "response":{"status":200,"statusText":"OK","httpVersion":"h2","headers":[{"name":"Set-Cookie","value":"sid=xyz; Path=/"}],"content":{"size":500,"mimeType":"application/json; charset=utf-8","text":"{\"ok\":true}"}}},
      {"startedDateTime":"2026-01-01T00:00:00.300Z","time":80,
       "request":{"method":"GET","url":"https://api.shop.test/v1/items/67890","headers":[],"queryString":[]},
       "response":{"status":503,"headers":[],"content":{"size":10,"mimeType":"text/plain"}}},
      {"startedDateTime":"2026-01-01T00:00:00.500Z","time":20,
       "request":{"method":"POST","url":"http://www.google-analytics.com/collect","headers":[{"name":"Cookie","value":"_ga=GA1.2.3; _gid=GA1.4.5"}],"queryString":[]},
       "response":{"status":204,"headers":[],"content":{"size":0,"mimeType":""}}}
    ]}}
    """;

    [Fact]
    public void Har_GroupsEndpointsRedactsAndFindsTrackers()
    {
        var f = PathOf("t.har");
        File.WriteAllText(f, Har);
        Assert.Equal(FileKind.Har, FileKindDetector.Detect(f).Kind);

        var m = HarReader.Read(f, null, default);

        Assert.Equal(3, m.TotalEntries);
        Assert.Equal("WebInspector 537.36", m.Creator);
        var ep = Assert.Single(m.Endpoints, e => e.Template == "/v1/items/{id}");
        Assert.Equal(2, ep.Count);
        Assert.Equal(1, ep.Errors);
        Assert.Equal(1, m.Errors5xx);
        Assert.Equal(520.0, m.SpanMs);

        var first = m.Entries[0];
        Assert.DoesNotContain("SECRET123", first.Url);
        Assert.Contains("lang=th", first.Url);
        Assert.Equal("Bearer •••", first.RequestHeaders.Single(h => h.Name == "Authorization").Value);
        Assert.DoesNotContain("SECRET123", first.Query.Single(q => q.Name == "token").Value);
        Assert.Equal("application/json", first.MimeType);
        Assert.Equal("2001:db8::1", first.ServerIp);
        Assert.True(first.HasCredentials);
        Assert.Equal("_ga=•••; _gid=•••", m.Entries[2].RequestHeaders[0].Value);

        Assert.Equal("Google Analytics", m.Hosts.Single(h => h.Host == "www.google-analytics.com").Tracker);
        Assert.Contains(m.Insights, i => i.Code == "HarTrackers");
        Assert.Contains(m.Insights, i => i.Code == "HarPlainHttp");
        Assert.Contains(m.Insights, i => i.Code == "HarCredsOverHttp");
        Assert.Contains(m.Insights, i => i.Code == "HarWeakCookies");
        Assert.True(m.RedactedValues >= 4);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/v1/users/42/orders", "/v1/users/{id}/orders")]
    [InlineData("/u/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/u/{id}")]
    [InlineData("/static/app.9f86d081884c7d65.js", "/static/app.9f86d081884c7d65.js")]
    [InlineData("/api/v2/search", "/api/v2/search")]
    public void Har_Template(string path, string expected) => Assert.Equal(expected, HarReader.Template(path));

    [Fact]
    public void Har_NotHar_IsRejected()
    {
        var f = PathOf("x.har");
        File.WriteAllText(f, "{\"hello\":1}");
        Assert.Equal(AnalysisFailureKind.InputRejected, Assert.Throws<AnalysisFailure>(() => LocalAnalyzer.Analyze(f, AnalysisKind.Web, null, default)).Kind);
    }

    // ───────────── WASM ─────────────

    private static byte[] Name(string s) { var b = Encoding.UTF8.GetBytes(s); return new[] { (byte)b.Length }.Concat(b).ToArray(); }

    private static byte[] Section(byte id, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return new[] { id, (byte)body.Length }.Concat(body).ToArray();
    }

    private static byte[] Wasm() => new byte[] { 0, (byte)'a', (byte)'s', (byte)'m', 1, 0, 0, 0 }
        .Concat(Section(1, new byte[] { 1, 0x60, 0, 0 }))
        .Concat(Section(2, new byte[] { 1 }, Name("wasi_snapshot_preview1"), Name("sock_send"), new byte[] { 0, 0 }))
        .Concat(Section(3, new byte[] { 1, 0 }))
        .Concat(Section(5, new byte[] { 1, 1, 2, 4 }))
        .Concat(Section(7, new byte[] { 1 }, Name("run"), new byte[] { 0, 1 }))
        .Concat(Section(10, new byte[] { 1, 2, 0, 0x0b }))
        .Concat(Section(11, new byte[] { 1, 0, 0x41, 0, 0x0b, 20 }, Encoding.ASCII.GetBytes("https://x.test/conf!")))
        .Concat(Section(0, Name("name"), new byte[] { 1, 6, 1, 1 }, Name("run")))
        .Concat(Section(0, Name("producers"), new byte[] { 1 }, Name("processed-by"), new byte[] { 1 }, Name("rustc"), Name("1.80")))
        .ToArray();

    [Fact]
    public void Wasm_ReadsImportsExportsMemoryNamesAndToolchain()
    {
        var f = PathOf("t.wasm");
        File.WriteAllBytes(f, Wasm());
        Assert.Equal(FileKind.Wasm, FileKindDetector.Detect(f).Kind);

        var m = WasmReader.Read(f, null, default);

        Assert.Equal(1u, m.Version);
        Assert.Equal(1, m.Types);
        Assert.Equal(1, m.DefinedFunctions);
        Assert.Equal(1, m.ImportedFunctions);
        Assert.Equal(new WasmImport("wasi_snapshot_preview1", "sock_send", "func"), Assert.Single(m.Imports));
        Assert.Equal(new WasmExport("run", "func", 1), Assert.Single(m.Exports));
        Assert.Equal(2ul, m.MemoryMinPages);
        Assert.Equal(4ul, m.MemoryMaxPages);
        Assert.Equal(new[] { "run" }, m.FunctionNames);
        Assert.Contains("processed-by: rustc 1.80", m.Producers);
        Assert.Equal("Rust", m.Toolchain);
        Assert.Contains(m.Strings, s => s.Contains("https://x.test/conf", StringComparison.Ordinal));
        Assert.Contains(m.Insights, i => i.Code == "WasmSockets");
        Assert.Contains(m.Sections, s => s.Name == "custom:name");
    }

    [Fact]
    public void Wasm_TruncatedSection_IsRejected()
    {
        var f = PathOf("bad.wasm");
        File.WriteAllBytes(f, new byte[] { 0, (byte)'a', (byte)'s', (byte)'m', 1, 0, 0, 0, 1, 50, 1 });
        Assert.Equal(AnalysisFailureKind.InputRejected, Assert.Throws<AnalysisFailure>(() => LocalAnalyzer.Analyze(f, AnalysisKind.Web, null, default)).Kind);
    }

    // ───────────── .NET metadata ─────────────

    [Fact]
    public void DotNetMeta_ReadsCoreAssembly()
    {
        var path = typeof(DotNetMetaReader).Assembly.Location;
        var m = DotNetMetaReader.Read(path, null, default);

        Assert.Equal("ReverseX.Core", m.Name);
        Assert.True(m.IsDll);
        Assert.True(m.ILOnly);
        Assert.StartsWith(".NETCoreApp", m.TargetFramework);
        Assert.Contains(m.Types, t => t.Namespace == "ReverseX.Core.Analysis.Local" && t.Name == "DotNetMetaReader" && t.Kind == "static class");
        Assert.Contains(m.References, r => r.Name == "System.Runtime");
        Assert.Contains(m.ApiUse, a => a.Category == "Process");
        Assert.Null(m.Obfuscator);
        Assert.True(m.UserStringCount > 0);
        Assert.NotEqual(Guid.Empty.ToString(), m.Mvid);
    }

    [Fact]
    public void DotNetMeta_NativeFile_IsRejected()
    {
        var f = PathOf("n.dll");
        File.WriteAllBytes(f, new byte[] { (byte)'M', (byte)'Z', 0, 0 });
        Assert.Equal(AnalysisFailureKind.InputRejected, Assert.Throws<AnalysisFailure>(() => LocalAnalyzer.Analyze(f, AnalysisKind.DotNetMeta, null, default)).Kind);
    }

    [Theory]
    [InlineData(FileKind.Apk, AnalysisKind.Android)]
    [InlineData(FileKind.Sqlite, AnalysisKind.Db)]
    [InlineData(FileKind.Har, AnalysisKind.Web)]
    [InlineData(FileKind.Wasm, AnalysisKind.Web)]
    [InlineData(FileKind.DotNet, AnalysisKind.DotNetMeta)]
    [InlineData(FileKind.Elf, null)]
    public void LocalAnalyzer_KindFor(FileKind file, AnalysisKind? expected) => Assert.Equal(expected, LocalAnalyzer.KindFor(file));

    [Fact]
    public void LocalAnalyzer_SnapshotCarriesHar()
    {
        var f = PathOf("t.har");
        File.WriteAllText(f, Har);
        var snap = LocalAnalyzer.Analyze(f, AnalysisKind.Web, null, default);
        Assert.Equal(AnalysisKind.Web, snap.Kind);
        Assert.NotNull(snap.Har);
        Assert.Equal("local", snap.ReaVersion);
    }
}
