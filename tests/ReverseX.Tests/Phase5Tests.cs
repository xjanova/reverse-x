using System.Text.Json;
using System.Text.Json.Nodes;
using ReverseX.Core.Ai;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Runs only when ANTHROPIC_API_KEY is set (spends a few cents); otherwise skipped with the reason.</summary>
public sealed class RealApiFactAttribute : FactAttribute
{
    public RealApiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            Skip = "ANTHROPIC_API_KEY is not set — real Claude API test skipped (the mock-based tests cover the tool loop for free)";
    }
}

public class Phase5Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-p5-" + Guid.NewGuid().ToString("N"));

    public Phase5Tests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---------- fakes ----------
    private sealed class FakeRea : IReaToolCaller
    {
        public List<(string Tool, string Args)> Calls { get; } = new();
        public string Reply { get; set; } = "{\"ok\":true}";
        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((tool, args.ToJsonString()));
            return Task.FromResult(new ReaToolResult(false, Reply, default));
        }
    }

    private sealed class Script : IModelStreamer
    {
        private readonly Queue<Func<TurnRequest, CancellationToken, Task<TurnResult>>> _turns = new();
        public List<TurnRequest> Requests { get; } = new();
        public Script Add(Func<TurnRequest, CancellationToken, Task<TurnResult>> t) { _turns.Enqueue(t); return this; }
        public Script Add(TurnResult r) => Add((_, _) => Task.FromResult(r));
        public Task<TurnResult> StreamTurnAsync(TurnRequest r, Action<string> onText, Action<string> onThinking, CancellationToken ct)
        {
            Requests.Add(r);
            if (_turns.Count == 0) throw new InvalidOperationException("script exhausted");
            return _turns.Dequeue()(r, ct);
        }
    }

    private static TurnResult Text(string t, AiUsage? u = null) =>
        new(new AiMessage(AiRole.Assistant, new AiBlock[] { new AiText(t) }), AiStop.EndTurn, u ?? new AiUsage(100, 50));

    private static TurnResult Call(string id, string tool, string json, AiUsage? u = null) =>
        new(new AiMessage(AiRole.Assistant, new AiBlock[] { new AiThinking("hm", "sig"), new AiToolUse(id, tool, json) }), AiStop.ToolUse, u ?? new AiUsage(100, 50));

    private static ReaTool Tool(string name) =>
        new(name, "d", JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}").RootElement.Clone());

    private (AiAgent Agent, List<AgentEvent> Events, FakeRea Rea) Make(Script s, string? target = null, string? own = null, params string[] tools)
    {
        var events = new List<AgentEvent>();
        var rea = new FakeRea();
        var box = new AgentToolbox(rea, tools.Select(Tool).ToList(), target, own, events.Add);
        return (new AiAgent(s, box, events.Add), events, rea);
    }

    // ---------- cost ----------
    [Fact]
    public void Cost_UsesListPricePerModel()
    {
        var u = new AiUsage(Input: 1_000_000, Output: 100_000, CacheWrite5m: 200_000, CacheWrite1h: 0, CacheRead: 500_000);
        // opus 5.5: 4 + 2.0 (20 x 0.1M) + 1.0 (5 x 0.2M) + 0.1 (0.2 x 0.5M)
        Assert.Equal(4m + 2m + 1m + 0.1m, CostCalculator.Cost(AiModels.Get("claude-opus-5-5"), u));
        Assert.Equal(2m + 1m + 0.5m + 0.05m, CostCalculator.Cost(AiModels.Get("claude-sonnet-5-5"), u));
        Assert.Equal(10m + 5m + 2.5m + 0.125m, CostCalculator.Cost(AiModels.Get("claude-fable-5-1"), u));
    }

    [Fact]
    public void Cost_HaikuSwitchesTierOverHundredThousandPromptTokens()
    {
        var h = AiModels.Get("claude-haiku-5-5");
        Assert.Equal(0.01m + 0.05m, CostCalculator.Cost(h, new AiUsage(100_000, 100_000)));   // 0.10 / 0.50 per MTok
        Assert.Equal(0.10m + 0.25m, CostCalculator.Cost(h, new AiUsage(200_000, 100_000)));   // 0.50 / 2.50 per MTok
    }

    [Fact]
    public void Cost_FormatNeverShowsZeroForSmallAmounts()
    {
        Assert.Equal("$0.0012", CostCalculator.Format(0.0012m));
        Assert.Equal("$1.50", CostCalculator.Format(1.5m));
    }

    [Fact]
    public void Models_AreTheFourRequested_DefaultOpus()
    {
        Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1" }, AiModels.All.Select(m => m.Id));
        Assert.Equal("claude-opus-5-5", new AiPrefs().Model);
    }

    // ---------- shrinking ----------
    [Fact]
    public void Shrink_SmallResultUntouched()
    {
        var store = new ResultStore();
        Assert.Equal("{\"a\":1}", ResultShrinker.Shrink("{\"a\":1}", store));
    }

    [Fact]
    public void Shrink_BigJsonArray_IsCompactedAndOriginalPageable()
    {
        var arr = new JsonArray();
        for (var i = 0; i < 2000; i++) arr.Add(new JsonObject { ["name"] = "module" + i, ["size"] = i });
        var text = new JsonObject { ["modules"] = arr }.ToJsonString();
        var store = new ResultStore();
        var shrunk = ResultShrinker.Shrink(text, store);
        Assert.True(shrunk.Length < text.Length / 5);
        Assert.Contains("อีก 1975 รายการ", shrunk);
        Assert.Contains("get_result_page", shrunk);
        var page1 = ResultShrinker.Page(store, "r1", 1);
        Assert.StartsWith(text[..100], page1);
        Assert.Contains("[หน้า 1/", page1);
    }

    [Fact]
    public void Shrink_BigPlainText_IsCutAtLineAndPaged()
    {
        var text = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "line " + i + " lorem ipsum"));
        var store = new ResultStore();
        var shrunk = ResultShrinker.Shrink(text, store, 2000);
        Assert.True(shrunk.Length < 2400);
        Assert.Contains("ผลลัพธ์ใหญ่", shrunk);
        var last = ResultShrinker.PageCount(text.Length, 2000);
        Assert.Contains($"[หน้า {last}/{last}]", ResultShrinker.Page(store, "r1", last, 2000));
        Assert.Contains("หน้าไม่ถูกต้อง", ResultShrinker.Page(store, "r1", last + 1, 2000));
        Assert.Contains("ไม่พบ", ResultShrinker.Page(store, "zzz", 1));
    }

    // ---------- tool selection ----------
    [Fact]
    public void ToolSelector_OffersOnlyRelevantReadOnlyTools()
    {
        var all = new[] { "analyze_javascript_application", "inspect_managed_artifact", "open_binary", "install_adb_package", "start_adb_app", "inspect_artifact", "trace_application_feature" }
            .Select(Tool).ToList();
        var js = ToolSelector.Select(FileKind.Asar, all).Select(t => t.Name).ToList();
        Assert.Contains("analyze_javascript_application", js);
        Assert.DoesNotContain("open_binary", js);
        Assert.DoesNotContain("install_adb_package", js);
        var net = ToolSelector.Select(FileKind.DotNet, all).Select(t => t.Name).ToList();
        Assert.Contains("inspect_managed_artifact", net);
        Assert.DoesNotContain("analyze_javascript_application", net);
        Assert.All(new[] { FileKind.Asar, FileKind.DotNet, FileKind.Pe, FileKind.Apk }, k => Assert.True(ToolSelector.NamesFor(k).Count < 40));
        Assert.DoesNotContain(ToolSelector.NamesFor(FileKind.Pe), n => n.Contains("adb") || n.Contains("install") || n.Contains("extract"));
    }

    // ---------- tool loop (mock, free) ----------
    [Fact]
    public async Task Loop_RunsToolThenAnswers_AndKeepsThinkingBlocksInHistory()
    {
        var script = new Script().Add(Call("t1", "analyze_javascript_application", "{\"path\":\"C:\\\\app\\\\app.asar\"}")).Add(Text("เสร็จแล้ว"));
        var (agent, events, rea) = Make(script, target: "C:\\app\\app.asar", tools: "analyze_javascript_application");
        var r = await agent.RunAsync(Array.Empty<AiMessage>(), "ถาม", new AgentOptions { ModelId = "claude-sonnet-5-5" }, default);

        Assert.Equal(AgentOutcome.Done, r.Outcome);
        Assert.Single(rea.Calls);
        Assert.Equal("analyze_javascript_application", rea.Calls[0].Tool);
        Assert.Equal("analyze_javascript_application", events.OfType<StepStarted>().Single().Tool);
        Assert.True(events.OfType<StepFinished>().Single().Ok);
        var second = script.Requests[1].Messages;
        Assert.Contains(second[1].Blocks, b => b is AiThinking { Signature: "sig" });
        Assert.Contains(second[2].Blocks, b => b is AiToolResult { ToolUseId: "t1", IsError: false });
        Assert.Equal(4, r.History.Count);
        Assert.Equal(2 * (100 * 2m + 50 * 10m) / 1_000_000m, r.CostUsd);
        Assert.Equal(AgentOutcome.Done, events.OfType<Finished>().Single().Outcome);
    }

    [Fact]
    public async Task Loop_OffersOnlyTheSelectedTools_PlusLocalOnes()
    {
        var script = new Script().Add(Text("ok"));
        var (agent, _, _) = Make(script, tools: new[] { "open_binary" });
        await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), default);
        Assert.Equal(new[] { "open_binary", "record_finding", "get_result_page" }, script.Requests[0].Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Loop_HugeToolResult_IsShrunkBeforeGoingBack()
    {
        var script = new Script().Add(Call("t1", "inspect_analysis_view", "{}")).Add(Text("ok"));
        var (agent, _, rea) = Make(script, tools: "inspect_analysis_view");
        rea.Reply = new string('x', 500_000);
        await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), default);
        var result = script.Requests[1].Messages[2].Blocks.OfType<AiToolResult>().Single();
        Assert.True(result.Content.Length < 13_000);
        Assert.Contains("get_result_page", result.Content);
    }

    [Fact]
    public async Task Loop_RecordFinding_ProducesEvidenceWithLevels()
    {
        var script = new Script()
            .Add(Call("a", "record_finding", "{\"kind\":\"evidence\",\"level\":\"seen\",\"text\":\"handle ipc\",\"location\":\"main/ipc.js:131\"}"))
            .Add(Call("b", "record_finding", "{\"kind\":\"unknown\",\"text\":\"RTF?\"}"))
            .Add(Call("c", "record_finding", "{\"kind\":\"path\",\"text\":\"copy\",\"steps\":[\"renderer\",\"preload\",\"main\"]}"))
            .Add(Text("ok"));
        var (agent, events, _) = Make(script);
        await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), default);
        var f = events.OfType<FindingRecorded>().Select(e => e.Finding).ToList();
        Assert.Equal(EvidenceLevel.Seen, f[0].Level);
        Assert.Equal("main/ipc.js:131", f[0].Location);
        Assert.Equal(FindingKind.Unknown, f[1].Kind);
        Assert.Equal(3, f[2].Steps.Count);
    }

    [Fact]
    public async Task Loop_ToolPathOutsideProject_IsRefusedWithoutCallingRea()
    {
        var script = new Script().Add(Call("t1", "inspect_artifact", "{\"path\":\"C:\\\\Windows\\\\System32\\\\config\\\\SAM\"}")).Add(Text("ok"));
        var (agent, events, rea) = Make(script, target: Path.Combine(_dir, "app.asar"), tools: "inspect_artifact");
        await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), default);
        Assert.Empty(rea.Calls);
        Assert.False(events.OfType<StepFinished>().Single().Ok);
    }

    [Fact]
    public async Task Refusal_GivesThaiMessage_AndRollsHistoryBack()
    {
        var refused = new TurnResult(new AiMessage(AiRole.Assistant, new AiBlock[] { new AiText("partial") }), AiStop.Refusal, new AiUsage(10, 5), "cyber", "x");
        var (agent, events, _) = Make(new Script().Add(refused));
        var prior = new[] { AiMessage.User("เก่า"), new AiMessage(AiRole.Assistant, new AiBlock[] { new AiText("ตอบเก่า") }) };
        var r = await agent.RunAsync(prior, "q", new AgentOptions(), default);
        Assert.Equal(AgentOutcome.Refused, r.Outcome);
        Assert.Contains("ปฏิเสธ", r.Message);
        Assert.Contains("ไซเบอร์", r.Message);
        Assert.Equal(2, r.History.Count);
        Assert.Equal(AgentOutcome.Refused, events.OfType<Finished>().Single().Outcome);
    }

    [Fact]
    public async Task Cancel_MidStream_StopsAndRollsBack()
    {
        using var cts = new CancellationTokenSource();
        var script = new Script().Add(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Text("never"); });
        var (agent, events, _) = Make(script);
        var r = await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), cts.Token);
        Assert.Equal(AgentOutcome.Cancelled, r.Outcome);
        Assert.Empty(r.History);
        Assert.Equal(AgentOutcome.Cancelled, events.OfType<Finished>().Single().Outcome);
    }

    [Fact]
    public async Task Cancel_DuringToolCall_MarksStepAndStops()
    {
        using var cts = new CancellationTokenSource();
        var script = new Script().Add(Call("t1", "inspect_artifact", "{}"));
        var events = new List<AgentEvent>();
        var box = new AgentToolbox(new HangingRea(cts), new[] { Tool("inspect_artifact") }, null, null, events.Add);
        var r = await new AiAgent(script, box, events.Add).RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), cts.Token);
        Assert.Equal(AgentOutcome.Cancelled, r.Outcome);
        Assert.Contains("ยกเลิก", events.OfType<StepFinished>().Single().Summary);
    }

    private sealed class HangingRea : IReaToolCaller
    {
        private readonly CancellationTokenSource _cts;
        public HangingRea(CancellationTokenSource cts) => _cts = cts;
        public async Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            _cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        }
    }

    [Fact]
    public async Task Budget_StopsLoopOnceSpent()
    {
        var big = new AiUsage(Input: 1_000_000, Output: 0); // $2 on sonnet 5.5
        var script = new Script().Add(Call("t1", "inspect_artifact", "{}", big)).Add(Text("should not be reached"));
        var (agent, _, _) = Make(script, tools: "inspect_artifact");
        var r = await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions { ModelId = "claude-sonnet-5-5", BudgetUsd = 0.5m }, default);
        Assert.Equal(AgentOutcome.BudgetExceeded, r.Outcome);
        Assert.Single(script.Requests);
        Assert.Contains("งบ", r.Message);
        Assert.Equal(2m, r.CostUsd);
    }

    [Fact]
    public void MaxTokens_ShrinksWithRemainingBudget()
    {
        var opus = AiModels.Get("claude-opus-5-5");
        Assert.Equal(16_000, AiAgent.MaxTokensFor(opus, 5m, 16_000));
        Assert.Equal(5_000, AiAgent.MaxTokensFor(opus, 0.1m, 16_000)); // $0.10 / $20 per MTok
        Assert.Equal(1024, AiAgent.MaxTokensFor(opus, 0.0001m, 16_000));
    }

    [Fact]
    public async Task StepLimit_StopsRunawayLoop()
    {
        var script = new Script();
        for (var i = 0; i < 5; i++) script.Add(Call("t" + i, "inspect_artifact", "{}"));
        var (agent, _, _) = Make(script, tools: "inspect_artifact");
        var r = await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions { MaxTurns = 3 }, default);
        Assert.Equal(AgentOutcome.StepLimit, r.Outcome);
        Assert.Equal(3, script.Requests.Count);
    }

    [Fact]
    public async Task ApiFailure_BecomesThaiMessage()
    {
        var script = new Script().Add((_, _) => throw new NoApiKeyException());
        var (agent, _, _) = Make(script);
        var r = await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions(), default);
        Assert.Equal(AgentOutcome.Failed, r.Outcome);
        Assert.Contains("API key", r.Message);
        Assert.DoesNotContain("Exception", r.Message);
    }

    // ---------- own-project code (diff first, write only on confirm) ----------
    [Fact]
    public async Task Propose_ShowsDiff_WritesNothing_UntilApply()
    {
        var own = Path.Combine(_dir, "MyApp");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "a.txt"), "one\ntwo\nthree\n");
        var json = new JsonObject { ["path"] = "a.txt", ["new_content"] = "one\nTWO\nthree\n", ["explanation"] = "เปลี่ยนบรรทัดสอง" }.ToJsonString();
        var (agent, events, _) = Make(new Script().Add(Call("p", "propose_file_change", json)).Add(Text("เสนอแล้ว")), own: own);
        await agent.RunAsync(Array.Empty<AiMessage>(), "q", new AgentOptions { HasOwnProject = true }, default);

        var p = events.OfType<ProposalReady>().Single().Proposal;
        Assert.Contains("-two", p.Diff);
        Assert.Contains("+TWO", p.Diff);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(own, "a.txt"))); // untouched

        CodeChange.Apply(own, p);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(Path.Combine(own, "a.txt")));
    }

    [Fact]
    public void Apply_RefusesWhenFileChangedSinceProposal()
    {
        var own = Path.Combine(_dir, "P");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "a.txt"), "x");
        var p = CodeChange.Propose(own, "a.txt", "y", "e", out _);
        File.WriteAllText(Path.Combine(own, "a.txt"), "changed by user");
        Assert.Throws<InvalidOperationException>(() => CodeChange.Apply(own, p));
        Assert.Equal("changed by user", File.ReadAllText(Path.Combine(own, "a.txt")));
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData(".git/config")]
    [InlineData("sub/../../evil.txt")]
    public void Propose_RejectsPathsOutsideTheFolder(string rel)
    {
        var own = Path.Combine(_dir, "Q");
        Directory.CreateDirectory(own);
        CodeChange.Propose(own, rel, "x", "e", out var err);
        Assert.NotNull(err);
    }

    [Fact]
    public void Diff_NewFile_AndChangedLineHaveContext()
    {
        var d = UnifiedDiff.Make("n.cs", null, "a\nb\n");
        Assert.Contains("--- /dev/null", d);
        Assert.Contains("+a", d);
        var old = string.Join('\n', Enumerable.Range(1, 30).Select(i => "l" + i));
        var d2 = UnifiedDiff.Make("f", old, old.Replace("l15", "L15"));
        Assert.Contains("-l15", d2);
        Assert.Contains(" l12", d2);
        Assert.DoesNotContain(" l5\n", d2);
    }

    // ---------- API key storage ----------
    // built from pieces so no real-looking key literal sits in the repo
    private static readonly string FakeKey = "sk-" + "ant-" + "api03-" + "SECRETSECRETSECRET" + "-1234567890abcdef";

    [Fact]
    public void ApiKey_RoundTrips_AndNeverAppearsInPlainFiles()
    {
        var file = Path.Combine(_dir, "apikey.bin");
        var store = new ApiKeyStore(file);
        Assert.False(store.HasKey);
        store.Save("  " + FakeKey + "\n");
        Assert.True(store.HasKey);
        Assert.Equal(FakeKey, store.Load());

        var bytes = File.ReadAllBytes(file);
        Assert.DoesNotContain("SECRETSECRET", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("SECRETSECRET", System.Text.Encoding.Unicode.GetString(bytes));

        new AiPrefs { Model = "claude-sonnet-5-5" }.Save(Path.Combine(_dir, "ai.json"));
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => f != file))
            Assert.DoesNotContain("SECRETSECRET", File.ReadAllText(f));

        store.Delete();
        Assert.False(store.HasKey);
        Assert.Null(store.Load());
    }

    [Fact]
    public void ApiKey_CorruptFile_ReadsAsMissing_AndMaskHidesMiddle()
    {
        var file = Path.Combine(_dir, "bad.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        Assert.Null(new ApiKeyStore(file).Load());
        var m = ApiKeyStore.Mask(FakeKey);
        Assert.DoesNotContain("SECRET", m);
        Assert.StartsWith("sk-ant-", m);
    }

    [Fact]
    public void Prefs_RoundTrip_AndBadValuesFallBack()
    {
        var f = Path.Combine(_dir, "ai.json");
        new AiPrefs { Model = "claude-haiku-5-5", BudgetUsd = 2.5m }.Save(f);
        var p = AiPrefs.Load(f);
        Assert.Equal("claude-haiku-5-5", p.Model);
        Assert.Equal(2.5m, p.BudgetUsd);
        File.WriteAllText(f, "{\"Model\":\"nope\",\"BudgetUsd\":-3}");
        p = AiPrefs.Load(f);
        Assert.Equal(AiModels.DefaultId, p.Model);
        Assert.Equal(AiPrefs.DefaultBudgetUsd, p.BudgetUsd);
    }

    [Fact]
    public void NoKey_Message_ExplainsWhereToPutIt()
    {
        Assert.Contains("ตั้งค่า", ErrorText.NoKey);
        Assert.Contains("sk-ant-", ErrorText.NoKey);
    }

    // ---------- real API (only with ANTHROPIC_API_KEY) ----------
    [RealApiFact]
    public async Task RealApi_Haiku_StreamsAnAnswerAndReportsUsage()
    {
        var streamer = new AnthropicTurnStreamer(() => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var chunks = new List<string>();
        var req = new TurnRequest("claude-haiku-5-5", "Answer in one word.", Array.Empty<AiToolDef>(),
            new[] { AiMessage.User("Say OK") }, 256);
        var r = await streamer.StreamTurnAsync(req, chunks.Add, _ => { }, default);
        Assert.Equal(AiStop.EndTurn, r.Stop);
        Assert.True(r.Usage.Output > 0);
        Assert.NotEmpty(chunks);
        Assert.True(CostCalculator.Cost(AiModels.Get("claude-haiku-5-5"), r.Usage) > 0);
    }
}
