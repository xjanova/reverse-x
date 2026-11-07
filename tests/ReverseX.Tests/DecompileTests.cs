using System.Globalization;
using System.Reflection;
using ReverseX.Core.Decompile;
using Xunit;

namespace ReverseX.Tests;

/// <summary>.NET → C# through the ILSpy engine, run on this test assembly itself (its own methods are the known answer).</summary>
public sealed class DecompileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-dec-" + Guid.NewGuid().ToString("N")[..8]);

    public DecompileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // The body the decompiler must give back: a literal, a branch and a call that survive compilation.
    internal static string SampleForDecompiler(int x) => x > 2 ? "rx-sample-" + x.ToString(CultureInfo.InvariantCulture) : "small";

    private static string OwnAssembly => typeof(DecompileTests).Assembly.Location;

    private static string TokenOf(MemberInfo m) => "0x" + m.MetadataToken.ToString("X8", CultureInfo.InvariantCulture);

    private static MethodInfo SampleMethod =>
        typeof(DecompileTests).GetMethod(nameof(SampleForDecompiler), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("sample method missing");

    [Fact]
    public void Method_DecompilesToItsCSharpBody()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var code = d.Decompile(TokenOf(SampleMethod), CancellationToken.None);
        Assert.Contains("SampleForDecompiler", code);
        Assert.Contains("\"rx-sample-\"", code);
        Assert.Contains("\"small\"", code);
        Assert.DoesNotContain("class DecompileTests", code);
    }

    [Fact]
    public void PropertyGetter_DecompilesAsItsProperty()
    {
        var getter = typeof(DecompileTests).GetProperty(nameof(OwnAssembly), BindingFlags.NonPublic | BindingFlags.Static)?.GetMethod
                     ?? throw new InvalidOperationException("property missing");
        var code = DotNetDecompiler.Open(OwnAssembly).Decompile(TokenOf(getter), CancellationToken.None);
        Assert.Contains("string OwnAssembly", code);
        Assert.Contains("typeof(DecompileTests).Assembly.Location", code);
    }

    [Fact]
    public void DeclaringType_DecompilesTheWholeClass()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var code = d.DecompileDeclaringType(TokenOf(SampleMethod), CancellationToken.None);
        Assert.Contains("class DecompileTests", code);
        Assert.Contains("SampleForDecompiler", code);
        Assert.Contains("Method_DecompilesToItsCSharpBody", code);
    }

    [Fact]
    public void TypeToken_DecompilesTheType()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var code = d.Decompile(TokenOf(typeof(DecompileTests)), CancellationToken.None);
        Assert.Contains("class DecompileTests", code);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0x04000001")]  // a field: not something the page asks for
    [InlineData("0x06FFFFFF")]  // past the end of the MethodDef table
    [InlineData("0x06000000")]  // row 0 does not exist
    public void BadToken_IsATypedFailure(string token)
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var ex = Assert.Throws<DecompileFailure>(() => d.Decompile(token, CancellationToken.None));
        Assert.Equal(DecompileFailureKind.BadToken, ex.Kind);
    }

    [Fact]
    public void DeclaringType_OfATypeToken_IsBadToken()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var ex = Assert.Throws<DecompileFailure>(() => d.DecompileDeclaringType(TokenOf(typeof(DecompileTests)), CancellationToken.None));
        Assert.Equal(DecompileFailureKind.BadToken, ex.Kind);
    }

    [Fact]
    public void NotAnAssembly_IsNotManaged()
    {
        var junk = Path.Combine(_dir, "junk.dll");
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        var ex = Assert.Throws<DecompileFailure>(() => DotNetDecompiler.Open(junk));
        Assert.Equal(DecompileFailureKind.NotManaged, ex.Kind);
    }

    [Fact]
    public void Open_DoesNotKeepTheFileLocked()
    {
        var copy = Path.Combine(_dir, "copy.dll");
        File.Copy(OwnAssembly, copy);
        var d = DotNetDecompiler.Open(copy);
        File.Delete(copy);  // would throw on Windows if the image were still mapped
        Assert.Contains("SampleForDecompiler", d.Decompile(TokenOf(SampleMethod), CancellationToken.None));
    }

    [Fact]
    public void Cancelled_Throws()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => d.Decompile(TokenOf(SampleMethod), cts.Token));
    }

    [Fact]
    public void ExportProject_WritesACsprojAndSources()
    {
        var d = DotNetDecompiler.Open(OwnAssembly);
        var target = DotNetDecompiler.FreshExportFolder(_dir, Path.GetFileName(OwnAssembly));
        var reports = 0;
        d.ExportProject(target, new SyncProgress<(int, int)>(_ => reports++), CancellationToken.None);

        Assert.Single(Directory.GetFiles(target, "*.csproj"));
        var sources = Directory.GetFiles(target, "*.cs", SearchOption.AllDirectories);
        Assert.Contains(sources, f => File.ReadAllText(f).Contains("\"rx-sample-\"", StringComparison.Ordinal));
        Assert.True(reports > 0);
    }

    [Fact]
    public void ExportProject_RefusesANonEmptyFolder()
    {
        var target = Path.Combine(_dir, "taken");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "mine.txt"), "user data");
        var d = DotNetDecompiler.Open(OwnAssembly);
        var ex = Assert.Throws<DecompileFailure>(() => d.ExportProject(target, null, CancellationToken.None));
        Assert.Equal(DecompileFailureKind.Io, ex.Kind);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(target, "mine.txt")));
    }

    [Fact]
    public void FreshExportFolder_NeverReturnsAnExistingPath()
    {
        var first = DotNetDecompiler.FreshExportFolder(_dir, "App.dll");
        Assert.Equal(Path.Combine(_dir, "App-decompiled"), first);
        Directory.CreateDirectory(first);
        var second = DotNetDecompiler.FreshExportFolder(_dir, "App.dll");
        Assert.Equal(Path.Combine(_dir, "App-decompiled (2)"), second);
    }

    [Fact]
    public void Cap_CutsHugeText()
    {
        var huge = new string('x', DotNetDecompiler.MaxChars + 10);
        var cut = DotNetDecompiler.Cap(huge);
        Assert.StartsWith(new string('x', 100), cut);
        Assert.Contains("10 more characters cut", cut);
        Assert.Equal("short", DotNetDecompiler.Cap("short"));
    }

    // ───────────── local member list (when REA cannot list them) ─────────────

    [Fact]
    public void ManagedListing_ListsMethodsWithTheRealTokensAndSignatures()
    {
        var m = ManagedListing.Build(OwnAssembly, CancellationToken.None);
        Assert.True(m.BuiltLocally);
        Assert.Equal("ReverseX.Tests", m.Info.Name);
        Assert.StartsWith(".NETCoreApp,Version=v8.0", m.Info.TargetFramework);
        Assert.Equal(64, m.Info.Sha256.Length);
        Assert.Contains(m.Info.References, r => r.Name == "ReverseX.Core");

        var type = m.Types.Single(t => t.FullName == "ReverseX.Tests.DecompileTests");
        var sample = type.Methods.Single(x => x.Name == nameof(SampleForDecompiler));
        Assert.Equal(TokenOf(SampleMethod), sample.Token);
        Assert.Equal("(int) → string", sample.Signature);
        Assert.True(sample.HasBody);
        Assert.True(sample.IlSize > 0);
        Assert.Empty(sample.Anchors);

        // nested types keep the Outer+Inner shape; generic arity stays as metadata writes it
        Assert.Contains(m.Types, t => t.FullName == "ReverseX.Tests.DecompileTests+SyncProgress`1");
    }

    [Fact]
    public void ManagedListing_TokensDecompile()
    {
        var m = ManagedListing.Build(OwnAssembly, CancellationToken.None);
        var token = m.Types.Single(t => t.FullName == "ReverseX.Tests.DecompileTests").Methods.Single(x => x.Name == nameof(SampleForDecompiler)).Token;
        Assert.Contains("\"rx-sample-\"", DotNetDecompiler.Open(OwnAssembly).Decompile(token, CancellationToken.None));
    }

    [Fact]
    public void ManagedListing_NotAnAssembly_IsNotManaged()
    {
        var junk = Path.Combine(_dir, "junk2.dll");
        File.WriteAllBytes(junk, new byte[] { 0x4D, 0x5A, 1, 2, 3 });
        var ex = Assert.Throws<DecompileFailure>(() => ManagedListing.Build(junk, CancellationToken.None));
        Assert.Equal(DecompileFailureKind.NotManaged, ex.Kind);
    }

    [Fact]
    public void LocalDotNetSnapshot_IsADotNetSnapshotOfThatFile()
    {
        var s = ReverseX.Core.Analysis.AnalysisRunner.LocalDotNetSnapshot(OwnAssembly, CancellationToken.None);
        Assert.Equal(ReverseX.Core.Analysis.AnalysisKind.DotNet, s.Kind);
        Assert.Equal(Path.GetFullPath(OwnAssembly), s.SourcePath);
        Assert.Equal(new FileInfo(OwnAssembly).Length, s.SourceSize);
        Assert.True(s.DotNet is { BuiltLocally: true, MethodCount: > 10 });
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the count when the call returns.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
