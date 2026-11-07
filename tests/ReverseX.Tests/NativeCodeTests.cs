using System.Text.Json.Nodes;
using ReverseX.Core.Analysis;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Native pseudo-C / assembly from recorded real Ghidra 12.1.4 output (via REA 6.4.0), and the kept-alive REA session.</summary>
public sealed class NativeCodeTests
{
    private static string Fx(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static ReaToolResult Ok(string text) => new(false, text, default);
    private static ReaToolResult Err(string text) => new(true, text, default);

    private sealed class FakeSession : IReaSession
    {
        public List<string> Calls { get; } = new();
        public Func<string, JsonNode, CancellationToken, Task<ReaToolResult>> Handler { get; init; } =
            (_, _, _) => Task.FromResult(new ReaToolResult(false, "{}", default));
        public bool Disposed { get; private set; }

        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(tool + " " + args.ToJsonString());
            return Handler(tool, args, ct);
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    // ───────────── parsing real output ─────────────

    [Fact]
    public void ParseProcedures_RealGhidraList_MarksImportsAndThunks()
    {
        var procs = ReaResultParser.ParseProcedures(Fx("native-procs-ghidra.json"));
        Assert.Equal(6, procs.Count);
        var free = procs.Single(p => p.Address == "EXTERNAL:0x1");
        Assert.Equal("KERNEL32.DLL::FreeLibrary", free.Name);
        Assert.True(free.External);
        var entry = procs.Single(p => p.Name == "entry");
        Assert.Equal("0x140011ab0", entry.Address);
        Assert.False(entry.External);
        Assert.False(entry.Thunk);
        Assert.True(procs.Single(p => p.Name.EndsWith("::wcsncmp", StringComparison.Ordinal)).Thunk);
    }

    [Fact]
    public void ParseCodeText_RealPseudoC()
    {
        var c = ReaResultParser.ParseCodeText(Fx("native-pseudo-ghidra.json"));
        Assert.NotNull(c.Text);
        Assert.StartsWith("void FUN_140012294(void)", c.Text);
        Assert.Contains("GetSystemTimeAsFileTime(&local_res8);", c.Text);
        Assert.Equal("Ghidra 12.1.4", c.Provider);
        Assert.NotEmpty(c.Limitations);
    }

    [Fact]
    public void ParseCodeText_RealAssembly()
    {
        var c = ReaResultParser.ParseCodeText(Fx("native-asm-ghidra.json"));
        Assert.NotNull(c.Text);
        Assert.StartsWith("0x140012294: MOV qword ptr [RSP + 0x18], RBX", c.Text);
    }

    [Theory]
    [InlineData("{\"raw_result\":{\"value\":null,\"limitations\":[\"no body\"]}}")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ParseCodeText_NoText_IsNullNotAnException(string json)
    {
        Assert.Null(ReaResultParser.ParseCodeText(json).Text);
    }

    // ───────────── runner ─────────────

    [Fact]
    public async Task Runner_NativeCode_AsksTheRightToolWithTheAddress()
    {
        var rea = new FakeSession
        {
            Handler = (tool, _, _) => Task.FromResult(tool switch
            {
                "procedure_pseudo_code" => Ok(Fx("native-pseudo-ghidra.json")),
                "procedure_assembly" => Ok(Fx("native-asm-ghidra.json")),
                _ => Err("{}"),
            }),
        };
        var runner = new AnalysisRunner(rea);
        var pseudo = await runner.NativeCodeAsync("0x140012294", NativeCodeView.PseudoCode, CancellationToken.None);
        var asm = await runner.NativeCodeAsync("0x140012294", NativeCodeView.Assembly, CancellationToken.None);
        Assert.Contains("GetSystemTimeAsFileTime", pseudo.Text);
        Assert.Contains("MOV", asm.Text);
        Assert.Equal(new[] { "procedure_pseudo_code {\"procedure\":\"0x140012294\"}", "procedure_assembly {\"procedure\":\"0x140012294\"}" }, rea.Calls);
    }

    [Fact]
    public async Task Runner_NativeCode_NoFunctionThere_IsInputRejected()
    {
        var rea = new FakeSession { Handler = (_, _, _) => Task.FromResult(Err(Fx("native-bad-address.json"))) };
        var ex = await Assert.ThrowsAsync<AnalysisFailure>(() => new AnalysisRunner(rea).NativeCodeAsync("0x1", NativeCodeView.PseudoCode, CancellationToken.None));
        Assert.Equal(AnalysisFailureKind.InputRejected, ex.Kind);
    }

    [Fact]
    public async Task Runner_NativeCode_NoEngine_IsNotSupported()
    {
        var rea = new FakeSession { Handler = (_, _, _) => Task.FromResult(Err(Fx("native-no-engine.json"))) };
        var ex = await Assert.ThrowsAsync<AnalysisFailure>(() => new AnalysisRunner(rea).NativeCodeAsync("entry", NativeCodeView.PseudoCode, CancellationToken.None));
        Assert.Equal(AnalysisFailureKind.NotSupported, ex.Kind);
    }

    // ───────────── kept-alive session ─────────────

    private sealed class Harness : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "rx-tsc-" + Guid.NewGuid().ToString("N")[..8]);
        public readonly List<FakeSession> Started = new();
        public readonly List<string> Prepared = new();
        public Func<FakeSession> Make = () => new FakeSession();

        public Harness() => Directory.CreateDirectory(Dir);

        public string File(string name, string content = "x")
        {
            var p = Path.Combine(Dir, name);
            System.IO.File.WriteAllText(p, content);
            return p;
        }

        public TargetSessionCache Cache() => new(
            _ => { var s = Make(); Started.Add(s); return Task.FromResult<IReaSession>(s); },
            async (rea, path, ct) => { Prepared.Add(path); await rea.CallAsync("open_binary", new JsonObject { ["path"] = path }, TimeSpan.FromSeconds(1), ct); });

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Cache_SameFile_ReusesOneProcessAndOpensOnce()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        for (var i = 0; i < 3; i++)
            await cache.RunAsync(a, (rea, ct) => rea.CallAsync("procedure_pseudo_code", new JsonObject(), TimeSpan.FromSeconds(1), ct), CancellationToken.None);
        Assert.Single(h.Started);
        Assert.Single(h.Prepared);
        Assert.Equal(4, h.Started[0].Calls.Count);  // one open_binary + three requests
    }

    [Fact]
    public async Task Cache_OtherFile_EndsTheOldProcess()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        var b = h.File("b.exe");
        await using var cache = h.Cache();
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        await cache.RunAsync(b, (_, _) => Task.FromResult(2), CancellationToken.None);
        Assert.Equal(2, h.Started.Count);
        Assert.True(h.Started[0].Disposed);
        Assert.False(h.Started[1].Disposed);
    }

    [Fact]
    public async Task Cache_FileRebuiltInPlace_StartsFresh()
    {
        using var h = new Harness();
        var a = h.File("a.exe", "v1");
        await using var cache = h.Cache();
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        File.WriteAllText(a, "version two, longer");
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        Assert.Equal(2, h.Started.Count);
        Assert.True(h.Started[0].Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cache_DeadProcessOrTimeout_DropsTheSession(bool dead)
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        Exception boom = dead ? new ReaException("The REA process has exited.") : new TimeoutException("slow");
        await Assert.ThrowsAsync(boom.GetType(), () => cache.RunAsync<int>(a, (_, _) => throw boom, CancellationToken.None));
        Assert.True(h.Started[0].Disposed);
        Assert.False(cache.HasSession);
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        Assert.Equal(2, h.Started.Count);
    }

    [Theory]
    [InlineData(AnalysisFailureKind.ReaUnavailable)]
    [InlineData(AnalysisFailureKind.Timeout)]
    public async Task Cache_WrappedProcessFailure_AlsoDropsTheSession(AnalysisFailureKind kind)
    {
        // AnalysisRunner.AnalyzeAsync turns ReaException/TimeoutException into AnalysisFailure before the cache sees it
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        await Assert.ThrowsAsync<AnalysisFailure>(() =>
            cache.RunAsync<int>(a, (_, _) => throw new AnalysisFailure(kind, "gone"), CancellationToken.None));
        Assert.False(cache.HasSession);
        Assert.True(h.Started[0].Disposed);
    }

    [Fact]
    public async Task Cache_AnalysisFailure_KeepsTheSession()
    {
        // "no function at that address" is an answer, not a broken engine: Ghidra's analysis must survive it
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        await Assert.ThrowsAsync<AnalysisFailure>(() =>
            cache.RunAsync<int>(a, (_, _) => throw new AnalysisFailure(AnalysisFailureKind.InputRejected, "no fn"), CancellationToken.None));
        Assert.True(cache.HasSession);
        Assert.False(h.Started[0].Disposed);
    }

    [Fact]
    public async Task Cache_PrepareFails_DoesNotKeepAHalfOpenSession()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        h.Make = () => new FakeSession { Handler = (_, _, _) => throw new ReaException("could not start") };
        await using var cache = h.Cache();
        await Assert.ThrowsAsync<ReaException>(() => cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None));
        Assert.False(cache.HasSession);
        Assert.True(h.Started[0].Disposed);
    }

    [Fact]
    public async Task Cache_CallsRunOneAtATime()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        var inside = 0;
        var maxInside = 0;
        var gate = new TaskCompletionSource();
        async Task<int> Work(IReaToolCaller _, CancellationToken __)
        {
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            await gate.Task;
            Interlocked.Decrement(ref inside);
            return 1;
        }
        var first = cache.RunAsync(a, Work, CancellationToken.None);
        var second = cache.RunAsync(a, Work, CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task Cache_DisposeEndsTheProcessAndRefusesNewWork()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        var cache = h.Cache();
        await cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None);
        await cache.DisposeAsync();
        Assert.True(h.Started[0].Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.RunAsync(a, (_, _) => Task.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task Cache_CancelledWhileWaiting_LeavesTheRunningCallAlone()
    {
        using var h = new Harness();
        var a = h.File("a.exe");
        await using var cache = h.Cache();
        var gate = new TaskCompletionSource();
        var first = cache.RunAsync(a, async (_, _) => { await gate.Task; return 1; }, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var second = cache.RunAsync(a, (_, _) => Task.FromResult(2), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        gate.SetResult();
        Assert.Equal(1, await first);
        Assert.True(cache.HasSession);
    }
}
