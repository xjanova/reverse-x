using System.Text.Json.Nodes;
using ReverseX.Core.Analysis;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>APK → Java from recorded real JADX headless MCP 0.7.1 output (via REA 6.4.0); package and path renamed.</summary>
public sealed class AndroidJavaTests
{
    private const string Apk = @"C:\samples\app.apk";
    private const string Main = "com.example.app.MainActivity";

    private static string Fx(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static ReaToolResult Ok(string text) => new(false, text, default);
    private static ReaToolResult Err(string text) => new(true, text, default);

    private sealed class FakeRea : IReaToolCaller
    {
        public List<(string Tool, JsonNode Args)> Calls { get; } = new();
        public required Func<string, JsonNode, ReaToolResult> Handler { get; init; }
        public Action? OnCall { get; init; }

        public Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((tool, args));
            OnCall?.Invoke();
            return Task.FromResult(Handler(tool, args));
        }
    }

    private static string Arg(JsonNode args, string name) => args[name]?.ToString() ?? "";

    /// <summary>Answers like JADX did for the sample APK: the class, its two method bodies.</summary>
    private static FakeRea RealApk() => new()
    {
        Handler = (tool, args) => tool switch
        {
            "inspect_android_class" when Arg(args, "class_name") == Main => Ok(Fx("android-class-jadx.json")),
            "inspect_android_class" => Err(Fx("android-class-missing.json")),
            "inspect_android_method" when Arg(args, "method_name") == "onActivityResult" => Ok(Fx("android-method-jadx.json")),
            "inspect_android_method" => Ok(Fx("android-ctor-jadx.json")),
            _ => Err("{}"),
        },
    };

    // ───────────── parsing real output ─────────────

    [Fact]
    public void ParseClass_RealJadx_ListsMethodsWithOverloadsAndFields()
    {
        var c = ReaResultParser.ParseAndroidClass(Fx("android-class-jadx.json"));
        Assert.NotNull(c);
        Assert.Equal(Main, c.ClassName);
        Assert.Equal(new[] { "<init>", "onActivityResult" }, c.Methods.Select(m => m.Name));
        Assert.True(c.Methods[0].IsConstructor);
        var m = c.Methods[1];
        Assert.Equal("onActivityResult(int, int, android.content.Intent): void", m.Signature);
        Assert.Equal(0, m.OverloadIndex);
        Assert.False(m.IsConstructor);
        Assert.Equal(4, c.Fields.Count);
        Assert.Contains(c.Fields, f => f is { Name: "k", Type: "java.lang.String" });
        Assert.Equal("JADX headless 0.7.1", c.Provider);
        Assert.NotEmpty(c.Limitations);
    }

    [Fact]
    public void ParseMethod_RealJadx_IsJava()
    {
        var m = ReaResultParser.ParseAndroidMethod(Fx("android-method-jadx.json"));
        Assert.NotNull(m.Text);
        Assert.Contains("public final void onActivityResult(int i2, int i3, Intent intent) {", m.Text);
        Assert.Contains("super.onActivityResult(i2, i3, intent);", m.Text);
        Assert.Equal("java", m.Representation);
        Assert.False(m.FellBack);
        Assert.Equal("complete", m.SourceStatus);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"normalized_result\":{\"methods\":[]}}")]  // no class name
    public void ParseClass_Unreadable_IsNull(string json) => Assert.Null(ReaResultParser.ParseAndroidClass(json));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"normalized_result\":{\"source\":{\"status\":\"unavailable\",\"text\":\"\"}}}")]
    public void ParseMethod_NoText_IsNullNotAnException(string json) => Assert.Null(ReaResultParser.ParseAndroidMethod(json).Text);

    // ───────────── assembling a class ─────────────

    [Fact]
    public async Task ClassJava_PutsPackageFieldsAndEveryBodyTogether()
    {
        var rea = RealApk();
        var reports = new List<(int, int)>();
        var code = await new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, Main, new SyncProgress<(int, int)>(reports.Add), CancellationToken.None);

        Assert.StartsWith("package com.example.app;\n", code.Text);
        Assert.Contains("    java.lang.String k;\n", code.Text);
        Assert.Contains("super.onActivityResult(i2, i3, intent);", code.Text);
        Assert.True(code.Text.IndexOf("public a(int i2", StringComparison.Ordinal) < code.Text.IndexOf("onActivityResult(int i2", StringComparison.Ordinal),
            "bodies keep JADX's member order");
        Assert.False(code.Text.Contains('\r'), "one line-end style (\\n) for the whole class");
        Assert.Equal((2, 2, 0, 0), (code.MethodCount, code.MethodsShown, code.FellBack, code.NoBody));
        Assert.Equal("JADX headless 0.7.1", code.Provider);
        Assert.Equal((2, 2), reports[^1]);

        Assert.Equal(new[] { "inspect_android_class", "inspect_android_method", "inspect_android_method" }, rea.Calls.Select(c => c.Tool));
        var call = rea.Calls[2].Args;
        Assert.Equal(Path.GetFullPath(Apk), Arg(call, "path"));
        Assert.Equal(Main, Arg(call, "class_name"));
        Assert.Equal("onActivityResult", Arg(call, "method_name"));
        Assert.Equal("0", Arg(call, "overload_index"));
    }

    [Fact]
    public async Task ClassJava_UnknownClass_IsInputRejected()
    {
        var f = await Assert.ThrowsAsync<AnalysisFailure>(() =>
            new AnalysisRunner(RealApk()).AndroidClassJavaAsync(Apk, "no.such.Klass", null, CancellationToken.None));
        Assert.Equal(AnalysisFailureKind.InputRejected, f.Kind);
    }

    [Fact]
    public async Task ClassJava_InnerClassFromDex_RetriesWithJadxSpelling()
    {
        var rea = new FakeRea
        {
            Handler = (tool, args) => tool == "inspect_android_class"
                ? Arg(args, "class_name") == "com.example.app.MainActivity.Inner"
                    ? Ok(Fx("android-class-jadx.json").Replace("\"com.example.app.MainActivity\"", "\"com.example.app.MainActivity.Inner\"", StringComparison.Ordinal))
                    : Err(Fx("android-class-missing.json"))
                : Ok(Fx("android-method-jadx.json")),
        };
        var code = await new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, "com.example.app.MainActivity$Inner", null, CancellationToken.None);
        Assert.Equal(new[] { "com.example.app.MainActivity$Inner", "com.example.app.MainActivity.Inner" },
            rea.Calls.Where(c => c.Tool == "inspect_android_class").Select(c => Arg(c.Args, "class_name")));
        Assert.StartsWith("package com.example.app;\n", code.Text);  // not "package com.example.app.MainActivity;"
    }

    [Fact]
    public async Task ClassJava_NoJadxOnThisMachine_IsNotSupported()
    {
        var rea = new FakeRea { Handler = (_, _) => Err(Fx("android-noengine.json")) };
        var f = await Assert.ThrowsAsync<AnalysisFailure>(() => new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, Main, null, CancellationToken.None));
        Assert.Equal(AnalysisFailureKind.NotSupported, f.Kind);
    }

    [Fact]
    public async Task ClassJava_SmaliFallbackAndMissingBodies_AreCountedAndMarked()
    {
        var rea = new FakeRea
        {
            Handler = (tool, args) => tool == "inspect_android_class"
                ? Ok(Fx("android-class-jadx.json"))
                : Arg(args, "method_name") == "<init>"
                    ? Ok("{\"normalized_result\":{\"representation\":\"smali\",\"fell_back\":true,\"source\":{\"status\":\"complete\",\"text\":\".method public constructor <init>()V\"}}}")
                    : Ok("{\"normalized_result\":{\"representation\":\"java\",\"body_status\":\"unavailable\",\"source\":null}}"),
        };
        var code = await new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, Main, null, CancellationToken.None);
        Assert.Equal((1, 1), (code.FellBack, code.NoBody));
        Assert.Contains(".method public constructor <init>()V", code.Text);
        Assert.Contains("    /* onActivityResult(int, int, android.content.Intent): void */", code.Text);
    }

    [Fact]
    public async Task ClassJava_HugeClass_StopsAtTheCap()
    {
        var methods = new JsonArray();
        for (var i = 0; i < AnalysisRunner.MaxClassMethods + 20; i++)
            methods.Add(new JsonObject { ["name"] = "m" + i, ["reported_signature"] = $"m{i}(): void", ["overload_index"] = 0 });
        var cls = new JsonObject { ["normalized_result"] = new JsonObject { ["class_name"] = "a.B", ["methods"] = methods } }.ToJsonString();
        var rea = new FakeRea { Handler = (tool, _) => Ok(tool == "inspect_android_class" ? cls : Fx("android-method-jadx.json")) };

        var code = await new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, "a.B", null, CancellationToken.None);
        Assert.Equal(AnalysisRunner.MaxClassMethods + 20, code.MethodCount);
        Assert.Equal(AnalysisRunner.MaxClassMethods, code.MethodsShown);
        Assert.Equal(AnalysisRunner.MaxClassMethods, rea.Calls.Count(c => c.Tool == "inspect_android_method"));
    }

    [Fact]
    public async Task ClassJava_CancelledBetweenMethods_Stops()
    {
        using var cts = new CancellationTokenSource();
        var rea = new FakeRea
        {
            Handler = RealApk().Handler,
            OnCall = () => cts.Cancel(),  // cancelled right after the class call
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisRunner(rea).AndroidClassJavaAsync(Apk, Main, null, cts.Token));
        Assert.Single(rea.Calls);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
