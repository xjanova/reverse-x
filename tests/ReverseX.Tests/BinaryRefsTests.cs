using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ReverseX.App.ViewModels;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using Xunit;

namespace ReverseX.Tests;

/// <summary>
/// Runs a test body on its own Dispatcher thread, as the app runs its view models: every await in the view model comes back to
/// this one thread, so the test decides the order in which engine answers arrive. No window and no Application (Loc gives keys).
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d));
            d.InvokeAsync(async () =>
            {
                try { await body(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                finally { d.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("UI test did not finish");
        error?.Throw();
    }

    /// <summary>Lets the dispatcher run the view model's continuations until <paramref name="done"/> holds (or fails the test).</summary>
    public static async Task Until(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > until) Assert.Fail("timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>Gives anything already queued a chance to run (to show that it did not change what is checked next).</summary>
    public static async Task Settle()
    {
        for (var i = 0; i < 5; i++) await Task.Delay(10);
    }
}

/// <summary>The Binary page's refs card for native functions: callers / callees after the code, stale answers ignored, errors local.</summary>
public sealed class BinaryRefsTests
{
    private const string Entry = "0x140001300", Main = "0x140001140", Init = "0x140001554", Leaf = "0x140001454", Import = "EXTERNAL:0x14";

    /// <summary>Every engine request waits until the test answers it; calls are recorded in the order the page made them.</summary>
    private sealed class FakeBackend : IAnalysisBackend
    {
        private readonly object _lock = new();
        private readonly List<(string Key, TaskCompletionSource<NativeCode> Tcs)> _code = new();
        private readonly List<(string Key, TaskCompletionSource<NativeRefs> Tcs)> _refs = new();
        private readonly List<string> _calls = new();

        public static readonly List<NativeProcedure> Procedures = new()
        {
            new(Import, "KERNEL32.DLL::GetModuleHandleW", External: true),
            new(Main, "FUN_140001140"),
            new(Entry, "entry"),
            new(Leaf, "FUN_140001454"),
            new(Init, "FUN_140001554"),
        };

        public IReadOnlyList<string> Calls { get { lock (_lock) return _calls.ToList(); } }

        public Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct) =>
            Task.FromResult(new AnalysisSnapshot
            {
                SourcePath = path,
                CreatedUtc = DateTime.UtcNow,
                Kind = AnalysisKind.Native,
                Native = new NativeModel { Procedures = Procedures, ProcedureTotal = Procedures.Count },
            });

        public Task<NativeCode> NativeCodeAsync(string path, string procedure, NativeCodeView view, CancellationToken ct) =>
            Add(_code, "code " + procedure + (view == NativeCodeView.Assembly ? " asm" : ""));

        public Task<NativeRefs> NativeRefsAsync(string path, string procedure, CancellationToken ct) => Add(_refs, "refs " + procedure);

        public Task<string> CallGraphAsync(string path, string address, CancellationToken ct) => throw new NotSupportedException();

        public Task<AndroidClassCode> AndroidClassJavaAsync(string apk, string className, IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        private Task<T> Add<T>(List<(string Key, TaskCompletionSource<T> Tcs)> list, string key)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) { _calls.Add(key); list.Add((key, tcs)); }
            return tcs.Task;
        }

        private TaskCompletionSource<T> Take<T>(List<(string Key, TaskCompletionSource<T> Tcs)> list, string key)
        {
            lock (_lock)
            {
                var i = list.FindIndex(x => x.Key == key);
                Assert.True(i >= 0, "no pending request: " + key);
                var tcs = list[i].Tcs;
                list.RemoveAt(i);
                return tcs;
            }
        }

        public void Code(string address, string text, bool asm = false) =>
            Take(_code, "code " + address + (asm ? " asm" : "")).SetResult(new NativeCode(text, Array.Empty<string>(), "Ghidra 12.1.4"));

        public void CodeFail(string address, Exception ex) => Take(_code, "code " + address).SetException(ex);

        public void Refs(string address, string[] callers, string[] callees) =>
            Take(_refs, "refs " + address).SetResult(new NativeRefs(callers, callees, Array.Empty<string>(), "Ghidra 12.1.4"));

        public void RefsFail(string address, Exception ex) => Take(_refs, "refs " + address).SetException(ex);
    }

    private sealed class Page : IDisposable
    {
        private readonly TempDir _dir = new();
        public readonly FakeBackend Backend = new();
        public readonly BinaryViewModel Vm;
        public readonly string Path;

        public Page()
        {
            Path = _dir.File("sample.exe");
            File.WriteAllBytes(Path, new byte[] { 0x4d, 0x5a });
            Vm = new BinaryViewModel(Backend, new SnapshotStore(_dir.File("projects")), null);
        }

        /// <summary>Opens the file (a live analysis, so the page opens on entry) and waits for entry's code request.</summary>
        public async Task OpenAsync()
        {
            await Vm.OpenAsync(Path, FileKind.Pe);
            await UiThread.Until(() => Backend.Calls.Contains("code " + Entry), "entry's code request");
        }

        public SymbolItem Item(string address) => Vm.Items.Single(i => i.Key == address);
        public int Count(string call) => Backend.Calls.Count(c => c == call);
        public RefRow Row(string text) => Vm.Refs.Single(r => r.IsRow && r.Text == text);

        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public void Refs_ArriveAfterTheCode_WithNamesFromTheList() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        Assert.Equal(p.Item(Entry), p.Vm.Selected);
        // while the code loads: no refs request yet, and only the refs card says it is busy
        Assert.DoesNotContain(p.Backend.Calls, c => c.StartsWith("refs", StringComparison.Ordinal));
        Assert.True(p.Vm.RefsBusy);
        Assert.False(p.Vm.HasRefs);
        Assert.Equal("Nat.Refs.Loading", p.Vm.NoRefsText);

        p.Backend.Code(Entry, "void entry(void) {}");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Entry), "the refs request");
        Assert.Equal("void entry(void) {}", p.Vm.CodeText);
        Assert.False(p.Vm.CodeBusy);
        Assert.Equal(new[] { "code " + Entry, "refs " + Entry }, p.Backend.Calls);

        p.Backend.Refs(Entry, Array.Empty<string>(), new[] { Import, Main, Init, "0x140099999" });
        await UiThread.Until(() => !p.Vm.RefsBusy, "refs shown");
        Assert.True(p.Vm.HasRefs);
        Assert.Equal("void entry(void) {}", p.Vm.CodeText);
        Assert.Equal(new[] { "Nat.Refs.Callers", "Nat.Refs.Empty", "Nat.Refs.Callees", "FUN_140001140", "FUN_140001554", "0x140099999", "KERNEL32.DLL::GetModuleHandleW" },
            p.Vm.Refs.Select(r => r.Text));
        Assert.True(p.Vm.Refs[0].IsHeader);
        Assert.Equal(BinaryViewModel.CalleeMark, p.Row("FUN_140001140").Kind);
        Assert.Same(p.Item(Main), p.Row("FUN_140001140").Target);
        Assert.False(p.Row("0x140099999").CanOpen);  // not in the list
        Assert.False(p.Row("KERNEL32.DLL::GetModuleHandleW").CanOpen);  // an import: shown, never opened
        Assert.Contains(p.Vm.Evidence, e => e.Text == "Nat.Sum.Refs");
    });

    [Fact]
    public void Refs_StaleAnswerForAnEarlierSelection_IsIgnored() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Entry), "entry's refs request");

        // the user moves on while entry's refs are still out
        p.Vm.Selected = p.Item(Main);
        await UiThread.Until(() => p.Backend.Calls.Contains("code " + Main), "the second function's code request");
        p.Backend.Refs(Entry, Array.Empty<string>(), new[] { Init });
        await UiThread.Settle();
        Assert.False(p.Vm.HasRefs);
        Assert.True(p.Vm.RefsBusy);
        Assert.Equal("Nat.Refs.Loading", p.Vm.NoRefsText);
        Assert.Equal("", p.Vm.CodeText);

        p.Backend.Code(Main, "main code");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Main), "the second function's refs request");
        p.Backend.Refs(Main, new[] { Entry }, new[] { Leaf });
        await UiThread.Until(() => !p.Vm.RefsBusy, "refs shown");
        Assert.Equal(BinaryViewModel.CallerMark, p.Row("entry").Kind);
        Assert.Equal(BinaryViewModel.CalleeMark, p.Row("FUN_140001454").Kind);
        Assert.DoesNotContain(p.Vm.Refs, r => r.Text == "FUN_140001554");

        // the late answer was still entry's: going back shows it at once, without asking the engine again
        p.Vm.Selected = p.Item(Entry);
        Assert.Equal(BinaryViewModel.CalleeMark, p.Row("FUN_140001554").Kind);
        Assert.False(p.Vm.RefsBusy);
        await UiThread.Until(() => p.Count("code " + Entry) == 2, "entry's code request again");
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Vm.CodeText == "entry code", "entry's code again");
        await UiThread.Settle();
        Assert.Equal(1, p.Count("refs " + Entry));
    });

    [Theory]
    [InlineData(AnalysisFailureKind.Generic)]
    [InlineData(AnalysisFailureKind.Timeout)]
    [InlineData(AnalysisFailureKind.ReaUnavailable)]
    public void Refs_Failure_LeavesTheCodeIntact_AndIsAskedAgainNextTime(AnalysisFailureKind kind) => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Entry), "the refs request");
        var note = p.Vm.CodeNote;

        p.Backend.RefsFail(Entry, new AnalysisFailure(kind, "engine said no"));
        await UiThread.Until(() => !p.Vm.RefsBusy, "the refs error");
        Assert.Equal("entry code", p.Vm.CodeText);
        Assert.Equal(note, p.Vm.CodeNote);
        Assert.False(p.Vm.CodeBusy);
        Assert.False(p.Vm.HasRefs);
        Assert.Equal("Nat.Refs.Err", p.Vm.NoRefsText);

        // a failed lookup is not remembered: picking the function again asks once more (after its code)
        p.Vm.Selected = p.Item(Main);
        p.Vm.Selected = p.Item(Entry);
        Assert.True(p.Vm.RefsBusy);
        await UiThread.Until(() => p.Count("code " + Entry) == 2, "entry's code again");
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Count("refs " + Entry) == 2, "entry's refs again");
    });

    [Theory]
    [InlineData(AnalysisFailureKind.Generic, true)]          // the decompiler gave up on this one function: its refs are still there
    [InlineData(AnalysisFailureKind.Timeout, false)]         // the engine session was dropped: asking again restarts the analysis
    [InlineData(AnalysisFailureKind.ReaUnavailable, false)]
    [InlineData(AnalysisFailureKind.InputRejected, false)]   // no function at that address
    public void Refs_AfterACodeFailure_OnlyWhenTheEngineCanStillAnswer(AnalysisFailureKind kind, bool asked) => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Backend.CodeFail(Entry, new AnalysisFailure(kind, "engine said no"));
        await UiThread.Until(() => !p.Vm.CodeBusy, "the code error");
        await UiThread.Settle();
        Assert.Equal(asked, p.Backend.Calls.Contains("refs " + Entry));
        Assert.Equal(asked, p.Vm.RefsBusy);
        if (!asked) Assert.Equal("Nat.Refs.Err", p.Vm.NoRefsText);
    });

    [Fact]
    public void Refs_StaleCodeAnswer_DoesNotStartARefsRequest() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Vm.Selected = p.Item(Main);
        await UiThread.Until(() => p.Backend.Calls.Contains("code " + Main), "the second code request");
        p.Backend.Code(Entry, "late");  // entry's code arrives after the user moved on
        await UiThread.Settle();
        Assert.Equal("", p.Vm.CodeText);
        Assert.DoesNotContain("refs " + Entry, p.Backend.Calls);
    });

    [Fact]
    public void ClickingARef_SelectsThatFunction_EvenWhenTheSearchHidesIt() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Entry), "the refs request");
        p.Backend.Refs(Entry, Array.Empty<string>(), new[] { Main, Import });
        await UiThread.Until(() => !p.Vm.RefsBusy, "refs shown");

        var import = p.Row("KERNEL32.DLL::GetModuleHandleW");
        Assert.False(import.CanOpen);  // the button is disabled; a stray Execute does nothing either
        p.Vm.OpenRefCommand.Execute(import);
        Assert.Equal(p.Item(Entry), p.Vm.Selected);

        p.Vm.Search = "entry";
        p.Vm.Tab = BinTab.Native;
        var row = p.Row("FUN_140001140");
        Assert.True(row.CanOpen);
        p.Vm.OpenRefCommand.Execute(row);
        Assert.Equal("", p.Vm.Search);
        Assert.Equal(Main, p.Vm.Selected?.Key);
        Assert.Contains(p.Vm.Selected!, p.Vm.Items);
        Assert.True(p.Vm.ShowCode);
        await UiThread.Until(() => p.Backend.Calls.Contains("code " + Main), "the picked function's code request");
    });

    [Fact]
    public void Imports_AreNeverSentToTheEngine() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Vm.Selected = p.Vm.Items.Single(i => i.Key == Import);
        await UiThread.Settle();
        Assert.Equal(new[] { "code " + Entry }, p.Backend.Calls);
        Assert.False(p.Vm.RefsBusy);
        Assert.Equal("Nat.Refs.Import", p.Vm.NoRefsText);
    });

    [Fact]
    public void SwitchingToAssembly_KeepsTheRefs_WithoutAskingAgain() => UiThread.Run(async () =>
    {
        using var p = new Page();
        await p.OpenAsync();
        p.Backend.Code(Entry, "entry code");
        await UiThread.Until(() => p.Backend.Calls.Contains("refs " + Entry), "the refs request");
        p.Backend.Refs(Entry, Array.Empty<string>(), new[] { Main });
        await UiThread.Until(() => !p.Vm.RefsBusy, "refs shown");

        p.Vm.ViewAsm = true;
        Assert.True(p.Vm.HasRefs);  // no flash while the assembly loads
        Assert.False(p.Vm.RefsBusy);
        await UiThread.Until(() => p.Backend.Calls.Contains("code " + Entry + " asm"), "the assembly request");
        p.Backend.Code(Entry, "0x140001300: SUB RSP,0x28", asm: true);
        await UiThread.Until(() => p.Vm.CodeText.StartsWith("0x140001300", StringComparison.Ordinal), "the assembly");
        await UiThread.Settle();
        Assert.Equal(1, p.Count("refs " + Entry));
        Assert.Equal(BinaryViewModel.CalleeMark, p.Row("FUN_140001140").Kind);
    });

    [Fact]
    public void DotNetRefs_StillListTheCilCalls() => UiThread.Run(async () =>
    {
        using var dir = new TempDir();
        var path = dir.File("lib.dll");
        File.WriteAllBytes(path, new byte[] { 0x4d, 0x5a });
        var method = new DotNetMethod
        {
            Token = "0x06000001", Name = "Run", DeclaringType = "Sample.App", HasBody = true,
            Calls = { new DotNetCall("Console::WriteLine", "member_ref", null, "call") },
            FieldAccesses = { "ldsfld _count" },
        };
        var snap = new AnalysisSnapshot
        {
            SourcePath = path, CreatedUtc = DateTime.UtcNow, Kind = AnalysisKind.DotNet,
            DotNet = new DotNetModel { Types = { new DotNetType { Token = "0x02000002", Name = "App", FullName = "Sample.App", Methods = { method } } } },
        };
        var store = new SnapshotStore(dir.File("projects"));
        store.Save(snap);
        var vm = new BinaryViewModel(new FakeBackend(), store, null);
        await vm.OpenAsync(path, FileKind.DotNet);
        Assert.Equal("Bin.Refs", vm.RefsTitle);
        Assert.Equal(new[] { ("Console::WriteLine", "call"), ("ldsfld _count", "field") }, vm.Refs.Select(r => (r.Text, r.Kind)));
        Assert.All(vm.Refs, r => Assert.False(r.CanOpen));
        Assert.False(vm.RefsBusy);
    });
}
