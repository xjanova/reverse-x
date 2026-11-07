using System.Text.Json.Nodes;
using ReverseX.Core.Analysis;
using ReverseX.Core.Engines;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>
/// Skips unless REA can run and Ghidra 12.1.x with JDK 21 is installed in the user's real data folder (the tests' own
/// AppPaths point at a throw-away folder, so the engines are looked up explicitly; they are only read).
/// </summary>
public sealed class GhidraFactAttribute : FactAttribute
{
    public static readonly EngineEnvironment Engines =
        new EngineLocator(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReverseX")).Resolve();

    public GhidraFactAttribute()
    {
        if (ReaEnv.SkipReason is { } reason) Skip = reason;
        else if (Engines is { GhidraDir: null } or { JavaHome: null }) Skip = "Ghidra 12.1.x and JDK 21 are not installed";
    }
}

/// <summary>Real Ghidra through REA on a small PE: the call lists the Binary page shows. Slow (Ghidra analyzes the whole file first).</summary>
public sealed class NativeRefsEngineTests
{
    private sealed class Session(ReaClient client) : IReaToolCaller
    {
        public readonly List<string> Calls = new();

        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add(tool);
            return client.CallToolAsync(tool, args, timeout, ct);
        }
    }

    /// <summary>REVERSEX_NATIVE_SAMPLE picks the file; otherwise the test host's own apphost (a native x64 PE built with the repo).</summary>
    private static string Sample()
    {
        var env = Environment.GetEnvironmentVariable("REVERSEX_NATIVE_SAMPLE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return new[] { "ReverseX.Tests.exe", "ReverseX.exe" }.Select(n => Path.Combine(AppContext.BaseDirectory, n)).First(File.Exists);
    }

    [GhidraFact]
    public async Task Entry_CalleesAndCallers_AreRealFunctionsThatPointBack()
    {
        var path = Sample();
        await using var client = await ReaClient.StartAsync(new ReaClientOptions
        {
            Node = ReaEnv.Node!,
            RequestTimeout = AnalysisRunner.NativeLong,
            Engines = GhidraFactAttribute.Engines,
        });
        var rea = new Session(client);
        var runner = new AnalysisRunner(rea);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        var snap = await runner.AnalyzeAsync(path, FileKind.Pe, null, cts.Token);
        Assert.False(snap.Native!.EngineMissing);
        var procs = snap.Native.Procedures;
        var entry = procs.First(p => p.Name == "entry" && !p.External);

        // the page's order: the code first (this waits for Ghidra's whole-file analysis), then the refs
        var code = await runner.NativeCodeAsync(entry.Address, NativeCodeView.PseudoCode, cts.Token);
        Assert.False(string.IsNullOrEmpty(code.Text));

        var refs = await runner.NativeRefsAsync(entry.Address, cts.Token);
        Assert.NotEmpty(refs.Callees);
        Assert.StartsWith("Ghidra", refs.Provider);
        var listed = procs.Select(p => p.Address).ToHashSet(StringComparer.Ordinal);
        Assert.All(refs.Callees, a => Assert.Contains(a, listed));

        // a function entry calls is called by entry
        var callee = refs.Callees.First(a => procs.Any(p => p.Address == a && !p.External));
        var back = await runner.NativeRefsAsync(callee, cts.Token);
        Assert.Contains(entry.Address, back.Callers);
        Assert.Equal(new[]
        {
            "open_binary", "list_procedures", "procedure_pseudo_code",
            "procedure_callers", "procedure_callees", "procedure_callers", "procedure_callees",
        }, rea.Calls);
    }
}
