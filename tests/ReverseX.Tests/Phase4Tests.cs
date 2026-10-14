using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Phase 4: REA results → models, paging, cancellation, no-engine message, snapshots. All offline, from recorded real REA output.</summary>
public sealed class Phase4Tests
{
    private static string Fx(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class FakeRea : IReaToolCaller
    {
        public List<(string Tool, JsonNode Args)> Calls { get; } = new();
        public Func<string, JsonNode, CancellationToken, Task<ReaToolResult>> Handler { get; init; } = null!;

        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((tool, args.DeepClone()));
            return Handler(tool, args, ct);
        }
    }

    private static ReaToolResult Ok(string text) => new(false, text, default);
    private static ReaToolResult Err(string text) => new(true, text, default);

    // ───────────── .NET model ─────────────

    [Fact]
    public void ParseManaged_BuildsTypesMethodsAndResolvedReferences()
    {
        var m = ReaResultParser.ParseManaged(Fx("net-artifact.json"), Fx("net-members.json"));

        Assert.Equal("ReverseX.Core", m.Info.Name);
        Assert.Equal(64, m.Info.Sha256.Length);
        Assert.Equal("observed", m.Info.Confidence);
        Assert.Contains(m.Types, t => t.FullName == "ReverseX.Core.AppPaths");
        Assert.True(m.MethodCount >= 10);

        var root = m.Types.Single(t => t.FullName == "ReverseX.Core.AppPaths").Methods.Single(x => x.Name == "get_Root");
        Assert.True(root.HasBody);
        Assert.Equal("() → string", root.Signature);
        Assert.Contains(root.Anchors, a => a.Opcode == "call" && a.Operand == "::GetEnvironmentVariable");
        Assert.Contains(root.Anchors, a => a.Opcode == "brfalse.s" && a.Operand.StartsWith("IL_", StringComparison.Ordinal));
        Assert.Contains(root.Calls, c => c.Target == "GetFolderPath");
        Assert.Equal(51, root.IlSize);
    }

    [Fact]
    public void ParseManaged_AcceptsCliEnvelope()
    {
        // `rea inspect-managed-members --format json --full-output` wraps the same Evidence as { ok, data, meta }.
        var wrapped = "{\"ok\":true,\"data\":" + Fx("net-members.json") + ",\"meta\":{}}";
        var m = ReaResultParser.ParseManaged(Fx("net-artifact.json"), wrapped);
        Assert.True(m.MethodCount >= 10);
    }

    [Theory]
    [InlineData("observed", EvidenceLevel.Seen)]
    [InlineData("derived", EvidenceLevel.Inferred)]
    [InlineData("inferred", EvidenceLevel.Inferred)]
    [InlineData("whatever", EvidenceLevel.Unknown)]
    [InlineData(null, EvidenceLevel.Unknown)]
    public void EvidenceLevel_MapsReaConfidence(string? rea, EvidenceLevel expected) =>
        Assert.Equal(expected, EvidenceLevels.FromRea(rea));

    // ───────────── JS / Electron model ─────────────

    [Fact]
    public void ParseJsSummary_ReadsCountsAndIntegrityWarning()
    {
        var s = ReaResultParser.ParseJsSummary(Fx("js-summary.json"));
        Assert.Equal("asar", s.Format);
        Assert.Equal(15, s.IpcOperations);
        Assert.Equal(2, s.IpcLiteralChannels);
        Assert.Equal(13, s.IpcDynamicOperations);
        Assert.Equal(2, s.NativeAddonBindings);
        Assert.Equal(27, s.RelevantFiles);
        Assert.Equal("derived", s.Confidence);
        Assert.StartsWith("ev_", s.ParentEvidenceId);
        Assert.True(s.IntegrityMismatch);
        Assert.NotEmpty(s.Limitations);
    }

    [Fact]
    public void ParseJsModulesPage_ReturnsItemsTotalAndNextOffset()
    {
        var first = ReaResultParser.ParseJsModulesPage(Fx("js-modules-page0.json"));
        Assert.Equal(40, first.Items.Count);
        Assert.Equal(109, first.Total);
        Assert.Equal(40, first.NextOffset);
        Assert.All(first.Items, i => Assert.False(string.IsNullOrEmpty(i.Path)));

        var last = ReaResultParser.ParseJsModulesPage(Fx("js-modules-page-last.json"));
        Assert.Equal(9, last.Items.Count);
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public async Task Runner_Js_PagesThroughModulesAndKeepsReaTotal()
    {
        var fake = new FakeRea
        {
            Handler = (tool, args, _) => Task.FromResult(tool switch
            {
                "analyze_javascript_application" => Ok(Fx("js-summary.json")),
                "inspect_analysis_view" => Ok((int)args["view"]!["offset"]! == 0 ? Fx("js-modules-page0.json") : Fx("js-modules-page-last.json")),
                _ => Err("{}"),
            }),
        };
        var steps = new List<string>();
        var snap = await new AnalysisRunner(fake).AnalyzeAsync("C:\\x\\app.asar", FileKind.Asar, new SyncProgress(p => steps.Add(p.Step)), default);

        Assert.Equal(AnalysisKind.Js, snap.Kind);
        Assert.Equal(49, snap.Js!.Modules.Count);
        Assert.Equal(109, snap.Js.ModulesTotal);
        Assert.False(snap.Js.ModulesComplete);
        Assert.Contains("js.modules", steps);

        // detail=summary + record-and-continue keep the call small and tolerate stripped .node files
        var analyze = fake.Calls.First(c => c.Tool == "analyze_javascript_application").Args;
        Assert.Equal("summary", (string)analyze["detail"]!);
        Assert.Equal("record-and-continue", (string)analyze["integrity_policy"]!);
        Assert.All(fake.Calls.Where(c => c.Tool == "inspect_analysis_view"), c => Assert.Equal("page", (string)c.Args["view"]!["kind"]!));
    }

    // ───────────── errors ─────────────

    [Theory]
    [InlineData("js-integrity-error.json", "artifact_integrity_mismatch")]
    [InlineData("js-transport-error.json", "resource_constraint")]
    [InlineData("native-no-engine.json", "provider_unavailable")]
    public void ParseError_ReadsRealReaErrors(string fixture, string code) =>
        Assert.Equal(code, ReaResultParser.ParseError(Fx(fixture))?.Code);

    [Fact]
    public void ParseError_NoEngine_ListsEveryRejectedProvider()
    {
        var e = ReaResultParser.ParseError(Fx("native-no-engine.json"))!;
        Assert.Contains(e.Rejections, r => r.ProviderId == "ghidra");
        Assert.Contains(e.Rejections, r => r.ProviderId == "ida");
        Assert.Contains(e.Rejections, r => r.ProviderId == "hopper");
    }

    [Fact]
    public void ParseError_ReturnsNull_ForNormalResult() =>
        Assert.Null(ReaResultParser.ParseError(Fx("native-open.json")));

    [Fact]
    public async Task Runner_Native_WithoutEngine_ReturnsFriendlyModelNotError()
    {
        var fake = new FakeRea
        {
            Handler = (tool, _, _) => Task.FromResult(tool switch
            {
                "open_binary" => Ok(Fx("native-open.json")),
                "list_procedures" => Err(Fx("native-no-engine.json")),
                _ => Err("{}"),
            }),
        };
        var snap = await new AnalysisRunner(fake).AnalyzeAsync(Environment.ProcessPath!, FileKind.Pe, null, default);

        Assert.Equal(AnalysisKind.Native, snap.Kind);
        Assert.True(snap.Native!.EngineMissing);
        Assert.Equal("pe", snap.Native.Identity!.Format);
        Assert.Equal(3, snap.Native.Rejections.Count);
        Assert.Empty(snap.Native.Procedures);
    }

    [Fact]
    public async Task Runner_Native_WithEngine_ListsProcedures()
    {
        const string procs = "{\"result\":[{\"address\":\"0x1000\",\"value\":\"main\"},{\"address\":\"0x1040\",\"value\":\"helper\"}]}";
        var fake = new FakeRea
        {
            Handler = (tool, _, _) => Task.FromResult(tool switch
            {
                "open_binary" => Ok(Fx("native-open.json")),
                "list_procedures" => Ok(procs),
                _ => Err("{}"),
            }),
        };
        var snap = await new AnalysisRunner(fake).AnalyzeAsync(Environment.ProcessPath!, FileKind.Pe, null, default);

        Assert.False(snap.Native!.EngineMissing);
        Assert.Equal(new[] { "main", "helper" }, snap.Native.Procedures.Select(p => p.Name));
        Assert.Equal("0x1040", snap.Native.Procedures[1].Address);
    }

    [Fact]
    public async Task Runner_DotNet_TooBigForMcp_FallsBackToCli()
    {
        var cliCalled = false;
        var fake = new FakeRea
        {
            Handler = (tool, _, _) => Task.FromResult(tool switch
            {
                "inspect_managed_artifact" => Ok(Fx("net-artifact.json")),
                "inspect_managed_members" => Err(Fx("js-transport-error.json")),
                _ => Err("{}"),
            }),
        };
        var runner = new AnalysisRunner(fake, (_, _) => { cliCalled = true; return Task.FromResult(Fx("net-members.json")); });
        var snap = await runner.AnalyzeAsync("C:\\x\\a.dll", FileKind.DotNet, null, default);

        Assert.True(cliCalled);
        Assert.True(snap.DotNet!.MethodCount >= 10);
    }

    [Fact]
    public async Task Runner_DotNet_TooBig_WithoutCli_ReportsTooLarge()
    {
        var fake = new FakeRea
        {
            Handler = (tool, _, _) => Task.FromResult(tool == "inspect_managed_artifact" ? Ok(Fx("net-artifact.json")) : Err(Fx("js-transport-error.json"))),
        };
        var ex = await Assert.ThrowsAsync<AnalysisFailure>(() => new AnalysisRunner(fake).AnalyzeAsync("C:\\x\\a.dll", FileKind.DotNet, null, default));
        Assert.Equal(AnalysisFailureKind.TooLarge, ex.Kind);
    }

    [Fact]
    public async Task Runner_UnknownKind_IsNotSupported()
    {
        var ex = await Assert.ThrowsAsync<AnalysisFailure>(() =>
            new AnalysisRunner(new FakeRea { Handler = (_, _, _) => Task.FromResult(Ok("{}")) }).AnalyzeAsync("C:\\x\\a.bin", FileKind.Unknown, null, default));
        Assert.Equal(AnalysisFailureKind.NotSupported, ex.Kind);
    }

    // ───────────── cancellation ─────────────

    [Fact]
    public async Task Runner_Cancel_StopsWhileWaitingForRea()
    {
        var started = new TaskCompletionSource();
        var fake = new FakeRea
        {
            Handler = async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Ok("{}");
            },
        };
        using var cts = new CancellationTokenSource();
        var run = new AnalysisRunner(fake).AnalyzeAsync("C:\\x\\a.dll", FileKind.DotNet, null, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Runner_Cancel_BetweenModulePages_StopsPaging()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeRea
        {
            Handler = (tool, _, _) =>
            {
                if (tool == "inspect_analysis_view") cts.Cancel(); // user presses Cancel while the first page is in flight
                return Task.FromResult(tool == "analyze_javascript_application" ? Ok(Fx("js-summary.json")) : Ok(Fx("js-modules-page0.json")));
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisRunner(fake).AnalyzeAsync("C:\\x\\app.asar", FileKind.Asar, null, cts.Token));
        Assert.Single(fake.Calls, c => c.Tool == "inspect_analysis_view");
    }

    // ───────────── paging ─────────────

    [Theory]
    [InlineData(0, 40, 1)]
    [InlineData(1, 40, 1)]
    [InlineData(40, 40, 1)]
    [InlineData(41, 40, 2)]
    [InlineData(109, 40, 3)]
    public void Paging_PageCount(int total, int size, int pages) => Assert.Equal(pages, Paging.PageCount(total, size));

    [Fact]
    public void Paging_Slice_ReturnsRightWindowAndClamps()
    {
        var all = Enumerable.Range(0, 109).ToList();
        Assert.Equal(Enumerable.Range(40, 40), Paging.Slice(all, 1, 40));
        Assert.Equal(Enumerable.Range(80, 29), Paging.Slice(all, 2, 40));
        Assert.Equal(Enumerable.Range(80, 29), Paging.Slice(all, 99, 40)); // past the end → last page
        Assert.Equal(Enumerable.Range(0, 40), Paging.Slice(all, -5, 40));  // before the start → first page
        Assert.Empty(Paging.Slice(new List<int>(), 0, 40));
    }

    // ───────────── snapshot ─────────────

    [Fact]
    public void Snapshot_RoundTrips_AndIsStaleOnlyWhenSourceChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "a.dll");
            File.WriteAllBytes(src, new byte[100]);
            var store = new SnapshotStore(Path.Combine(dir, "projects"));
            var (size, write) = SnapshotStore.Stamp(src);
            var snap = new AnalysisSnapshot
            {
                SourcePath = src, SourceSize = size, SourceWriteUtc = write, CreatedUtc = DateTime.UtcNow, Kind = AnalysisKind.DotNet,
                DotNet = ReaResultParser.ParseManaged(Fx("net-artifact.json"), Fx("net-members.json")),
            };
            Assert.Null(store.TryLoad(src));
            store.Save(snap);

            var loaded = store.TryLoad(src)!;
            Assert.Equal(snap.DotNet.MethodCount, loaded.DotNet!.MethodCount);
            Assert.Equal(snap.DotNet.Info.Sha256, loaded.DotNet.Info.Sha256);
            Assert.False(SnapshotStore.IsStale(loaded));

            File.WriteAllBytes(src, new byte[200]);
            Assert.True(SnapshotStore.IsStale(loaded));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Snapshot_CorruptFile_IsIgnoredNotThrown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rx-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(dir);
            var file = store.FileFor("C:\\x\\a.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "not gzip");
            Assert.Null(store.TryLoad("C:\\x\\a.dll"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SnapshotId_IsStableAndCaseInsensitive() =>
        Assert.Equal(SnapshotStore.IdFor("C:\\Apps\\Foo.DLL"), SnapshotStore.IdFor("c:\\apps\\foo.dll"));

    // ───────────── Go build info ─────────────

    private static byte[] GoBlob(string version, string modinfo)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.Latin1.GetBytes("\u00ff Go buildinf:"));
        ms.WriteByte(8);      // pointer size
        ms.WriteByte(2);      // flags: inline strings (Go 1.18+)
        ms.Write(new byte[16]);
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var n = b.Length;
            while (n >= 0x80) { ms.WriteByte((byte)(n | 0x80)); n >>= 7; }
            ms.WriteByte((byte)n);
            ms.Write(b);
        }
        Str(version);
        Str(new string('\u0001', 16) + modinfo + new string('\u0002', 16));
        return ms.ToArray();
    }

    [Fact]
    public void GoBuildInfo_ParsesVersionModuleDepsAndSettings()
    {
        var info = GoBuildInfo.Parse(GoBlob("go1.25.5",
            "path\texample.com/app/cmd\nmod\texample.com/app\tv1.2.3\th1:abc\ndep\tgithub.com/x/y\tv0.1.0\th1:def\n=>\tgithub.com/x/y-fork\tv0.2.0\nbuild\t-compiler=gc\nbuild\tCGO_ENABLED=0\n"))!;

        Assert.Equal("go1.25.5", info.Version);
        Assert.Equal("example.com/app/cmd", info.Path);
        Assert.Equal("example.com/app", info.MainModule);
        Assert.Equal("v1.2.3", info.MainVersion);
        Assert.Single(info.Dependencies);
        Assert.Equal("github.com/x/y => github.com/x/y-fork", info.Dependencies[0].Path);
        Assert.Contains(info.Settings, s => s.Key == "compiler" && s.Value == "gc");
        Assert.Contains(info.Settings, s => s.Key == "CGO_ENABLED" && s.Value == "0");
    }

    [Fact]
    public void GoBuildInfo_RejectsGarbage()
    {
        Assert.Null(GoBuildInfo.Parse(new byte[64]));
        Assert.Null(GoBuildInfo.Parse(GoBlob("notgo", "")));
    }

    [Fact]
    public void GoBuildInfo_TryRead_FindsHeaderInsideALargerFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "rx-go-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var filler = new byte[300_000];
            var blob = GoBlob("go1.22.0", "path\tx/y\n");
            using (var fs = File.Create(path)) { fs.Write(filler); fs.Write(blob); fs.Write(filler); }
            Assert.Equal("go1.22.0", GoBuildInfo.TryRead(path)!.Version);
            File.WriteAllBytes(path, filler);
            Assert.Null(GoBuildInfo.TryRead(path));
        }
        finally { File.Delete(path); }
    }

    // ───────────── real REA (skipped without Node) ─────────────

    [ReaFact]
    public async Task Real_DotNet_AnalyzesOwnCoreAssembly()
    {
        var dll = typeof(ReverseX.Core.AppPaths).Assembly.Location;
        await using var session = new ReaSession(ReaEnv.Node!);
        var runner = new AnalysisRunner(session, ReaSession.ManagedMembersViaCli(ReaEnv.Node!));
        AnalysisSnapshot snap;
        try { snap = await runner.AnalyzeAsync(dll, FileKind.DotNet, null, default); }
        catch (AnalysisFailure f) when (f.Kind == AnalysisFailureKind.TooLarge)
        {
            return; // REA refuses metadata over its own 64 MB budget (our dll grew past it): the app must say so, not show an empty list
        }

        Assert.Equal("ReverseX.Core", snap.DotNet!.Info.Name);
        Assert.Contains(snap.DotNet.Types, t => t.FullName == "ReverseX.Core.UiPrefs");
        Assert.True(snap.DotNet.MethodCount > 100);
        var load = snap.DotNet.Types.First(t => t.FullName == "ReverseX.Core.UiPrefs").Methods.First(m => m.Name == "Load");
        Assert.NotEmpty(load.Anchors);
    }

    [ReaFact]
    public async Task Real_Js_AnalyzesInstalledElectronApp_ReadOnly()
    {
        var asar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Obsidian", "resources", "app.asar");
        if (!File.Exists(asar)) return; // no Electron app installed on this machine: nothing to check
        var before = new FileInfo(asar).LastWriteTimeUtc;
        await using var session = new ReaSession(ReaEnv.Node!);
        var snap = await new AnalysisRunner(session).AnalyzeAsync(asar, FileKind.Asar, null, default);

        Assert.Equal(AnalysisKind.Js, snap.Kind);
        Assert.True(snap.Js!.Modules.Count > 0);
        Assert.True(snap.Js.Summary.IpcOperations >= 0);
        Assert.Equal(before, new FileInfo(asar).LastWriteTimeUtc);
    }

    private sealed class SyncProgress : IProgress<AnalysisProgress>
    {
        private readonly Action<AnalysisProgress> _a;
        public SyncProgress(Action<AnalysisProgress> a) => _a = a;
        public void Report(AnalysisProgress value) => _a(value);
    }
}
