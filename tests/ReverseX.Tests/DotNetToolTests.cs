using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ReverseX.Core.Ai;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>The assistant's local .NET tools (list_dotnet_members, decompile_dotnet), run on ReverseX.Core and this test assembly.</summary>
public sealed class DotNetToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-dnt-" + Guid.NewGuid().ToString("N")[..8]);

    public DotNetToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string Core = typeof(ResultStore).Assembly.Location;
    private static readonly string Own = typeof(DotNetToolTests).Assembly.Location;
    // one per assembly for the whole class: the type system is built once, as in a chat
    private static readonly DotNetCodeTools CoreCode = new(Core);
    private static readonly DotNetCodeTools OwnCode = new(Own);

    // known answers in this assembly: a nested type with two overloads
    internal static class Nested
    {
        internal static int Twice(int x) => x * 2;
        internal static int Twice(string s) => s.Length * 2;
    }

    private static string TokenOf(MemberInfo m) => "0x" + m.MetadataToken.ToString("X8", CultureInfo.InvariantCulture);

    private static MethodInfo Method(Type t, string name) =>
        t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new InvalidOperationException(name + " missing");

    private static string J(object o) => JsonSerializer.Serialize(o);

    // ---------- fakes ----------
    private sealed class FakeRea : IReaToolCaller
    {
        public List<string> Calls { get; } = new();
        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(tool);
            return Task.FromResult(new ReaToolResult(false, "{}", default));
        }
    }

    private sealed class Script : IModelStreamer
    {
        private readonly Queue<TurnResult> _turns = new();
        public List<TurnRequest> Requests { get; } = new();
        public Script Add(TurnResult r) { _turns.Enqueue(r); return this; }
        public Task<TurnResult> StreamTurnAsync(TurnRequest r, Action<string> onText, Action<string> onThinking, CancellationToken ct)
        {
            Requests.Add(r);
            if (_turns.Count == 0) throw new InvalidOperationException("script exhausted");
            return Task.FromResult(_turns.Dequeue());
        }
    }

    private static AgentToolbox Box(string? target, DotNetCodeTools? code = null, FileKind kind = FileKind.DotNet, FakeRea? rea = null, Action<AgentEvent>? emit = null) =>
        new(rea ?? new FakeRea(), Array.Empty<ReaTool>(), target, null, emit ?? (_ => { }), targetKind: kind, dotnetCode: code);

    // Assert.DoesNotContain on strings is culture-sensitive and "found" "   at " inside plain sentences here: compare ordinally
    private static void Lacks(string part, string text) => Assert.False(text.Contains(part, StringComparison.Ordinal), $"\"{part}\" found in: {text}");

    private static void AssertShortError(ToolOutput r)
    {
        Assert.True(r.IsError);
        Lacks("Exception", r.Text);
        Lacks("   at ", r.Text);
        Assert.True(r.Text.Length < 600, r.Text);
    }

    // ---------- offered only for .NET ----------
    [Fact]
    public void DotNetTarget_OffersBothLocalTools_WithoutAPathArgument()
    {
        var box = Box(Core, CoreCode);
        var defs = box.Definitions();
        Assert.True(box.HasDotNetCode);
        foreach (var name in new[] { AgentToolbox.ListDotNetMembers, AgentToolbox.DecompileDotNet })
        {
            var d = defs.Single(x => x.Name == name);
            Assert.True(box.IsLocal(name));
            var props = JsonNode.Parse(d.InputSchemaJson)!["properties"]!.AsObject().Select(p => p.Key).ToList();
            Assert.DoesNotContain(props, p => p.Contains("path") || p.Contains("file") || p.Contains("assembly"));
        }
    }

    [Theory]
    [InlineData(FileKind.Pe)]
    [InlineData(FileKind.Elf)]
    [InlineData(FileKind.Asar)]
    [InlineData(FileKind.Apk)]
    [InlineData(FileKind.Folder)]
    [InlineData(FileKind.Wasm)]
    [InlineData(FileKind.Unknown)]
    public async Task OtherKinds_DoNotGetTheTools_EvenForAManagedFile(FileKind kind)
    {
        var box = Box(Core, kind: kind);
        Assert.False(box.HasDotNetCode);
        Assert.DoesNotContain(box.Definitions(), d => d.Name is AgentToolbox.ListDotNetMembers or AgentToolbox.DecompileDotNet);
        var r = await box.ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore" }), default);
        Assert.True(r.IsError);
        Assert.Contains("not available", r.Text);
        Lacks("decompile_dotnet", AiAgent.SystemPrompt(new AgentOptions(), box.HasDotNetCode));
    }

    [Fact]
    public void DotNetKind_WithoutATarget_HasNoTools()
    {
        Assert.False(Box(null).HasDotNetCode);
        Assert.DoesNotContain(Box(null).Definitions(), d => d.Name == AgentToolbox.DecompileDotNet);
    }

    [Fact]
    public void SystemPrompt_PointsAtTheLocalTools_OnlyWhenOffered()
    {
        Assert.Contains("decompile_dotnet", AiAgent.SystemPrompt(new AgentOptions(), dotnetCode: true));
        Assert.Contains("list_dotnet_members", AiAgent.SystemPrompt(new AgentOptions(), dotnetCode: true));
        Lacks("decompile_dotnet", AiAgent.SystemPrompt(new AgentOptions()));
    }

    // ---------- listing ----------
    [Fact]
    public void List_WithoutFilter_HasTheAssemblyTypesMethodsAndTokens()
    {
        var r = CoreCode.List(null, false, default);
        Assert.False(r.IsError);
        Assert.StartsWith("Assembly ReverseX.Core", r.Text);
        Assert.Contains(TokenOf(typeof(ResultStore)) + " ReverseX.Core.Ai.ResultStore · methods: ", r.Text);
        Assert.Contains("  " + TokenOf(Method(typeof(ResultStore), nameof(ResultStore.Put))) + " public Put(string) → string", r.Text);
        Assert.Contains("public static PageCount(int, int) → int", r.Text);
        // big and unfiltered: a namespace map comes first so the model can narrow down
        Assert.Contains("Namespaces (types): ", r.Text);
        Assert.Contains("ReverseX.Core.Ai (", r.Text);
        Assert.False(r.Text.Contains('\r'));
        // compiler-generated types ("<>c__DisplayClass…", generated regexes) come after every real one
        var types = r.Text.Split('\n').Where(l => l.StartsWith("0x02", StringComparison.Ordinal)).ToList();
        var firstGenerated = types.FindIndex(l => l.Contains('<'));
        Assert.True(firstGenerated > 0);
        Assert.All(types.Skip(firstGenerated), l => Assert.True(l.Contains('<'), l));
    }

    [Fact]
    public void List_Filter_IsCaseInsensitive_AndKeepsOnlyMatches()
    {
        var r = CoreCode.List("RESULTSTORE", false, default);
        Assert.False(r.IsError);
        Assert.Contains("Filter \"RESULTSTORE\": ", r.Text);
        Assert.Contains("ReverseX.Core.Ai.ResultStore · methods: ", r.Text);
        Assert.Contains(" public Put(string) → string", r.Text);
        Lacks("ResultShrinker", r.Text);
        Lacks("Namespaces (types)", r.Text);
    }

    [Fact]
    public void List_FilterOnAMethodName_ShowsOnlyThatMethodOfItsType()
    {
        var r = CoreCode.List("pagecount", false, default);
        var lines = r.Text.Split('\n');
        var type = Assert.Single(lines, l => l.Contains(" ReverseX.Core.Ai.ResultShrinker · "));
        Assert.Contains(" match", type);
        Assert.Contains(lines, l => l.EndsWith(" public static PageCount(int, int) → int", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(" Shrink("));
    }

    [Fact]
    public void List_TypesOnly_LeavesTheMethodsOut()
    {
        var r = CoreCode.List("ResultS", true, default);
        Assert.Contains("ReverseX.Core.Ai.ResultStore · methods: ", r.Text);
        Assert.Contains("ReverseX.Core.Ai.ResultShrinker · methods: ", r.Text);
        Assert.DoesNotContain(r.Text.Split('\n'), l => l.StartsWith("  0x06", StringComparison.Ordinal));
    }

    [Fact]
    public void List_NoMatch_SaysSo_WithoutBeingAnError()
    {
        var r = CoreCode.List("zz-no-such-member-zz", false, default);
        Assert.False(r.IsError);
        Assert.Contains("Nothing matches \"zz-no-such-member-zz\"", r.Text);
    }

    [Fact]
    public async Task LongList_IsShrunk_AndTheRestIsPageable()
    {
        var full = CoreCode.List(null, false, default).Text;
        Assert.True(full.Length > 2 * ResultShrinker.DefaultPageChars);

        var box = Box(Core, CoreCode);
        var r = await box.ExecuteAsync(AgentToolbox.ListDotNetMembers, "{}", default);
        Assert.False(r.IsError);
        Assert.True(r.Text.Length < ResultShrinker.DefaultPageChars + 1_000);
        Assert.Contains("get_result_page(result_id=\"r1\"", r.Text);

        var page2 = await box.ExecuteAsync(AgentToolbox.GetResultPage, J(new { result_id = "r1", page = 2 }), default);
        Assert.False(page2.IsError);
        Assert.StartsWith(full.Substring(ResultShrinker.DefaultPageChars, 200), page2.Text);
        Assert.Contains("[หน้า 2/", page2.Text);
    }

    // ---------- decompile ----------
    [Fact]
    public async Task Decompile_TypeByFullName_GivesItsCSharp()
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore" }), default);
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("// ReverseX.Core.Ai.ResultStore · " + TokenOf(typeof(ResultStore)) + " · C# decompiled locally from ReverseX.Core.dll", r.Text);
        Assert.Contains("class ResultStore", r.Text);
        Assert.Contains("public string Put(string full)", r.Text);
        Assert.Contains("\"r\"", r.Text);
    }

    [Fact]
    public async Task Decompile_MethodByToken_GivesOnlyThatMethod()
    {
        var token = TokenOf(Method(typeof(ResultShrinker), nameof(ResultShrinker.PageCount)));
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { token }), default);
        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("// ReverseX.Core.Ai.ResultShrinker.PageCount(int, int) → int · " + token, r.Text);
        Assert.Contains("Math.Max(1, (total + pageChars - 1) / pageChars)", r.Text);
        Lacks("class ResultShrinker", r.Text);
    }

    [Fact]
    public async Task Decompile_TokenAsAPlainNumber_Works()
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { token = typeof(ResultStore).MetadataToken }), default);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("class ResultStore", r.Text);
    }

    [Fact]
    public async Task Decompile_WholeType_FromAMethodToken_GivesTheDeclaringClass()
    {
        var token = TokenOf(Method(typeof(ResultShrinker), nameof(ResultShrinker.PageCount)));
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { token, whole_type = true }), default);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("class ResultShrinker", r.Text);
        Assert.Contains("Shrink(", r.Text);
    }

    [Fact]
    public async Task Decompile_MethodByName_InANestedType_ReturnsEveryOverload()
    {
        var box = Box(Own, OwnCode);
        foreach (var typeName in new[] { "ReverseX.Tests.DotNetToolTests+Nested", "reversex.tests.dotnettooltests.nested", "DotNetToolTests+Nested" })
        {
            var r = await box.ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = typeName, method_name = "Twice" }), default);
            Assert.False(r.IsError, typeName + ": " + r.Text);
            Assert.Contains("// " + TokenOf(typeof(Nested).GetMethod("Twice", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int) })!) + " Twice(int) → int", r.Text);
            Assert.Contains("x * 2", r.Text);
            Assert.Contains("s.Length * 2", r.Text);
        }
    }

    [Fact]
    public async Task Decompile_UnknownType_IsAShortErrorPointingAtTheList()
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "No.Such.Type" }), default);
        AssertShortError(r);
        Assert.Contains("\"No.Such.Type\" was not found", r.Text);
        Assert.Contains("list_dotnet_members", r.Text);
    }

    [Fact]
    public async Task Decompile_AmbiguousShortName_AsksForOneToken()
    {
        // several test classes have their own nested FakeRea
        var r = await Box(Own, OwnCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "FakeRea" }), default);
        AssertShortError(r);
        Assert.Contains("call again with one token", r.Text);
        Assert.Contains("ReverseX.Tests.DotNetToolTests+FakeRea", r.Text);
    }

    [Fact]
    public async Task Decompile_UnknownMethod_ListsTheMethodsThatExist()
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore", method_name = "Nope" }), default);
        AssertShortError(r);
        Assert.Contains("has no method \"Nope\"", r.Text);
        Assert.Contains("Put", r.Text);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0x04000001")]   // a field
    [InlineData("0x06FFFFFF")]   // past the end of the MethodDef table
    public async Task Decompile_BadToken_IsAShortError(string token)
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { token }), default);
        AssertShortError(r);
        Assert.Contains("is not a type (0x02…) or method (0x06…) token", r.Text);
    }

    [Fact]
    public async Task Decompile_WithNothingToDecompile_SaysWhatToGive()
    {
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.DecompileDotNet, "{}", default);
        AssertShortError(r);
        Assert.Contains("type_name", r.Text);
    }

    [Fact]
    public async Task NotAManagedFile_IsAShortError()
    {
        var junk = Path.Combine(_dir, "junk.dll");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        var box = Box(junk);
        var list = await box.ExecuteAsync(AgentToolbox.ListDotNetMembers, "{}", default);
        AssertShortError(list);
        Assert.Contains("junk.dll has no .NET (CLI) metadata", list.Text);
        var dec = await box.ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "A.B" }), default);
        AssertShortError(dec);
        Assert.Contains("no .NET (CLI) metadata", dec.Text);
    }

    [Fact]
    public async Task MissingFile_IsAShortError()
    {
        var r = await Box(Path.Combine(_dir, "gone.dll")).ExecuteAsync(AgentToolbox.ListDotNetMembers, "{}", default);
        AssertShortError(r);
        Assert.Contains("could not read gone.dll", r.Text);
    }

    // ---------- only the opened file is read ----------
    [Theory]
    [InlineData("path", "C:\\Windows\\System32\\kernel32.dll")]
    [InlineData("assembly", "Other.dll")]
    [InlineData("file", "..\\ReverseX.Core.dll")]
    [InlineData("input_path", "\\\\server\\share\\ReverseX.Core.dll")]
    public async Task ModelSuppliedPath_ToAnotherFile_IsRefused(string key, string value)
    {
        var rea = new FakeRea();
        var args = new JsonObject { ["type_name"] = "ReverseX.Core.Ai.ResultStore", [key] = value }.ToJsonString();
        var r = await Box(Core, CoreCode, rea: rea).ExecuteAsync(AgentToolbox.DecompileDotNet, args, default);
        AssertShortError(r);
        Assert.Contains("takes no path", r.Text);
        Assert.Contains("\"" + key + "\"", r.Text);
        Assert.Empty(rea.Calls);
    }

    [Fact]
    public async Task ModelSuppliedPath_ArrayWithAnotherFile_IsRefused()
    {
        var args = new JsonObject { ["filter"] = "Result", ["paths"] = new JsonArray(Core, "C:\\Windows\\win.ini") }.ToJsonString();
        var r = await Box(Core, CoreCode).ExecuteAsync(AgentToolbox.ListDotNetMembers, args, default);
        AssertShortError(r);
    }

    [Fact]
    public async Task ModelSuppliedPath_ThatIsTheOpenedFile_IsHarmless()
    {
        var box = Box(Core, CoreCode);
        var full = await box.ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore", path = Core }), default);
        Assert.False(full.IsError, full.Text);
        Assert.Contains("class ResultStore", full.Text);
        var name = await box.ExecuteAsync(AgentToolbox.ListDotNetMembers, J(new { filter = "ResultStore", assembly = "reversex.core.dll" }), default);
        Assert.False(name.IsError, name.Text);
    }

    [Fact]
    public async Task AnInstanceForAnotherFile_IsNotUsed()
    {
        // the cached tools of the test assembly must not answer for a toolbox opened on ReverseX.Core
        var r = await Box(Core, OwnCode).ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore" }), default);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("from ReverseX.Core.dll", r.Text);
    }

    // ---------- cancellation ----------
    [Fact]
    public async Task Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var box = Box(Core, CoreCode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => box.ExecuteAsync(AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore" }), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => box.ExecuteAsync(AgentToolbox.ListDotNetMembers, "{}", cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => CoreCode.List(null, false, cts.Token));
    }

    // ---------- the agent loop ----------
    [Fact]
    public async Task Loop_ModelCallsDecompile_AndGetsCSharpBack()
    {
        var call = new TurnResult(new AiMessage(AiRole.Assistant, new AiBlock[]
            {
                new AiToolUse("t1", AgentToolbox.DecompileDotNet, J(new { type_name = "ReverseX.Core.Ai.ResultStore" })),
            }), AiStop.ToolUse, new AiUsage(100, 50));
        var done = new TurnResult(new AiMessage(AiRole.Assistant, new AiBlock[] { new AiText("ResultStore เก็บผลเต็มไว้") }), AiStop.EndTurn, new AiUsage(100, 50));
        var script = new Script().Add(call).Add(done);
        var events = new List<AgentEvent>();
        var rea = new FakeRea();
        var agent = new AiAgent(script, Box(Core, CoreCode, rea: rea, emit: events.Add), events.Add);

        var res = await agent.RunAsync(Array.Empty<AiMessage>(), "ResultStore ทำอะไร", new AgentOptions { ModelId = "claude-sonnet-5-5" }, default);

        Assert.Equal(AgentOutcome.Done, res.Outcome);
        Assert.Contains(script.Requests[0].Tools, t => t.Name == AgentToolbox.ListDotNetMembers);
        Assert.Contains(script.Requests[0].Tools, t => t.Name == AgentToolbox.DecompileDotNet);
        Assert.Contains("decompile_dotnet", script.Requests[0].System);
        var result = script.Requests[1].Messages[2].Blocks.OfType<AiToolResult>().Single();
        Assert.Equal("t1", result.ToolUseId);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("class ResultStore", result.Content);
        Assert.Empty(rea.Calls);   // answered locally, REA never asked
        Assert.Equal("ถอดโค้ด .NET เป็น C#", events.OfType<StepStarted>().Single().Title);
        Assert.True(events.OfType<StepFinished>().Single().Ok);
    }

    // ---------- UI strings ----------
    [Fact]
    public void EveryToolHeading_HasThaiAndEnglishStrings()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReverseX.App"))) dir = Path.GetDirectoryName(dir);
        foreach (var file in new[] { "Strings.th.xaml", "Strings.en.xaml" })
        {
            var keys = XDocument.Load(Path.Combine(dir!, "src", "ReverseX.App", "Resources", file)).Root!.Elements()
                .Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToHashSet();
            Assert.Contains(AgentToolbox.DecompileDotNet, ToolTitles.Known);
            Assert.All(ToolTitles.Known, t => Assert.Contains("S.Ai.Tool." + t, keys));
            Assert.Contains("S.Ai.NoRea.Net", keys);
        }
    }
}
