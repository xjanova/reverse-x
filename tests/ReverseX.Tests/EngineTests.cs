using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ReverseX.Core.Engines;
using ReverseX.Core.Rea;
using ReverseX.Core.Tools;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Engine catalog, locator, installer and launcher wiring. No real download: tiny in-memory ZIPs and a fake handler.</summary>
public sealed class EngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rx-eng-" + Guid.NewGuid().ToString("N"));

    public EngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Engines => Path.Combine(_root, "engines");
    private string AppGhidra => Path.Combine(Engines, EngineCatalog.Ghidra.FolderName);
    private string AppJdk => Path.Combine(Engines, EngineCatalog.Jdk.FolderName);
    private string AppJar => Path.Combine(Engines, EngineCatalog.JadxMcp.FolderName, EngineCatalog.JadxMcp.ProbeFile);

    // ---------- helpers ----------

    private static void Touch(string file, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
        File.WriteAllText(file, text);
    }

    private static void FakeGhidra(string dir, string version)
    {
        Touch(Path.Combine(dir, "support", "analyzeHeadless.bat"), "@echo off");
        Touch(Path.Combine(dir, "Ghidra", "application.properties"),
            $"application.name=Ghidra\napplication.version={version}\napplication.java.min=21\n");
    }

    private static void FakeJdk(string dir, string? version)
    {
        Touch(Path.Combine(dir, "bin", "java.exe"), "MZ");
        if (version is not null) Touch(Path.Combine(dir, "release"), $"IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"{version}\"\n");
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => d.TryGetValue(name, out var v) ? v : null;
    }

    private EngineEnvironment Resolve(params (string Name, string Value)[] vars) => new EngineLocator(_root, Env(vars)).Resolve();

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name, CompressionLevel.NoCompression).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] JdkZip() => Zip(
        ("jdk-21.0.13+9/bin/java.exe", "MZ"u8.ToArray()),
        ("jdk-21.0.13+9/release", "JAVA_VERSION=\"21.0.13\"\n"u8.ToArray()),
        ("jdk-21.0.13+9/lib/modules", RandomNumberGenerator.GetBytes(8 * 1024)));

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A real catalog entry (same folder/probe, so the locator recognises it) pinned to a test body.</summary>
    private static EngineSpec Pinned(EngineSpec real, byte[] body, string? sha = null, long? size = null) =>
        real with { Url = "https://engines.test/" + real.DownloadName, Size = size ?? body.LongLength, Sha256 = sha ?? Sha(body) };

    private EngineInstaller Installer(HttpMessageHandler handler, Func<string, long?>? freeBytes = null) =>
        new(_root, new HttpClient(handler), freeBytes ?? (_ => 1L << 40));

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        public static FakeHandler Serving(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) }));

        public static FakeHandler Streaming(Func<Stream> body) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) }));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return reply(request, ct);
        }
    }

    /// <summary>Non-seekable body (no Content-Length) that runs <paramref name="atLimit"/> once <paramref name="after"/> bytes were served.</summary>
    private sealed class Trickle(byte[] data, int after, Action atLimit) : Stream
    {
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= after) atLimit();
            var n = Math.Min(Math.Min(count, 8192), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Recorder : IProgress<EngineInstallProgress>
    {
        private readonly ConcurrentQueue<EngineInstallProgress> _items = new();
        public IReadOnlyList<EngineInstallProgress> Items => _items.ToArray();
        public void Report(EngineInstallProgress value) => _items.Enqueue(value);
    }

    private void AssertNoLeftovers()
    {
        Assert.False(Directory.Exists(Path.Combine(Engines, EngineInstaller.DownloadDirName)));
        if (!Directory.Exists(Engines)) return;
        Assert.Empty(Directory.GetDirectories(Engines, EngineInstaller.StagingPrefix + "*"));
        Assert.Empty(Directory.GetFiles(Engines, "*.part", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Engines, ".install.lock"));
    }

    // ---------- catalog ----------

    [Fact]
    public void Catalog_EntriesArePinned()
    {
        Assert.Equal(new[] { "ghidra", "jdk", "jadx-mcp" }, EngineCatalog.All.Select(s => s.Id));
        foreach (var s in EngineCatalog.All)
        {
            Assert.StartsWith("https://github.com/", s.Url);
            Assert.Matches("^[0-9a-f]{64}$", s.Sha256);
            Assert.True(s.Size > 1_000_000);
        }
        Assert.Equal("OpenJDK21U-jdk_x64_windows_hotspot_21.0.13_9.zip", EngineCatalog.Jdk.DownloadName);
        Assert.Equal("ghidra_12.1.4_PUBLIC_20260921.zip", EngineCatalog.Ghidra.DownloadName);
        Assert.Equal(EnginePackaging.SingleFile, EngineCatalog.JadxMcp.Packaging);
        Assert.Equal(EngineCatalog.JadxMcp.DownloadName, EngineCatalog.JadxMcp.ProbeFile);
    }

    // ---------- locator ----------

    [Fact]
    public void Locator_ValidUserGhidra_Wins()
    {
        var user = Path.Combine(_root, "user-ghidra");
        FakeGhidra(user, "12.1.3");
        FakeGhidra(AppGhidra, "12.1.4");

        var env = Resolve(("GHIDRA_INSTALL_DIR", user));
        Assert.Equal(user, env.GhidraDir);
        Assert.Equal(EngineSource.UserEnvironment, env.GhidraSource);
        Assert.Equal(user, env.Variables["GHIDRA_INSTALL_DIR"]);

        // A quoted value (common when set by hand) is still the user's choice, passed on without the quotes.
        Assert.Equal(user, Resolve(("GHIDRA_INSTALL_DIR", $"\"{user}\"")).GhidraDir);
    }

    [Fact]
    public void Locator_UserGhidra11_FallsBackToAppInstalled()
    {
        var user = Path.Combine(_root, "ghidra11");
        FakeGhidra(user, "11.4.2");
        FakeGhidra(AppGhidra, "12.1.4");

        var env = Resolve(("GHIDRA_INSTALL_DIR", user));
        Assert.Equal(AppGhidra, env.GhidraDir);
        Assert.Equal(EngineSource.AppInstalled, env.GhidraSource);
        Assert.Equal(AppGhidra, env.Variables["GHIDRA_INSTALL_DIR"]);
    }

    [Fact]
    public void Locator_UserGhidra11_NoAppCopy_IsNone_AndDoesNotTouchUserVariable()
    {
        var user = Path.Combine(_root, "ghidra11");
        FakeGhidra(user, "11.4.2");
        var env = Resolve(("GHIDRA_INSTALL_DIR", user));
        Assert.Null(env.GhidraDir);
        Assert.Equal(EngineSource.None, env.GhidraSource);
        Assert.False(env.Variables.ContainsKey("GHIDRA_INSTALL_DIR"));
    }

    [Fact]
    public void Locator_AppGhidraWithWrongVersion_IsNotUsed()
    {
        FakeGhidra(AppGhidra, "12.0.3");
        Assert.Null(Resolve().GhidraDir);
    }

    [Fact]
    public void Locator_Nothing_IsNone()
    {
        var env = Resolve(("PATH", @"C:\Windows"));
        Assert.Null(env.GhidraDir);
        Assert.Null(env.JavaHome);
        Assert.Null(env.JadxMcpJar);
        Assert.Equal(EngineSource.None, env.GhidraSource);
        Assert.Equal(EngineSource.None, env.JavaSource);
        Assert.Equal(EngineSource.None, env.JadxMcpSource);
        Assert.Empty(env.Variables);
        Assert.Equal(EngineCatalog.All, EngineCatalog.MissingFrom(env));
    }

    [Fact]
    public void Locator_AppJdk_PreferredOverUserJdk_AndPathPrepended()
    {
        FakeJdk(AppJdk, "21.0.13");
        var user = Path.Combine(_root, "user-jdk22");
        FakeJdk(user, "22.0.1");

        var env = Resolve(("JAVA_HOME", user), ("PATH", @"C:\Windows\system32;C:\tools"));
        Assert.Equal(AppJdk, env.JavaHome);
        Assert.Equal(EngineSource.AppInstalled, env.JavaSource);
        Assert.Equal(AppJdk, env.Variables["JAVA_HOME"]);
        Assert.Equal(Path.Combine(AppJdk, "bin") + Path.PathSeparator + @"C:\Windows\system32;C:\tools", env.Variables["PATH"]);
    }

    [Fact]
    public void Locator_UserJavaHome17_IsIgnored()
    {
        var user = Path.Combine(_root, "jdk17");
        FakeJdk(user, "17.0.12");
        var env = Resolve(("JAVA_HOME", user), ("PATH", @"C:\Windows"));
        Assert.Null(env.JavaHome);
        Assert.Equal(EngineSource.None, env.JavaSource);
        Assert.False(env.Variables.ContainsKey("JAVA_HOME"));
        Assert.False(env.Variables.ContainsKey("PATH"));
    }

    [Fact]
    public void Locator_UserJavaHome21_UsedWhenNoAppJdk()
    {
        var user = Path.Combine(_root, "jdk21");
        FakeJdk(user, "21.0.5");
        var env = Resolve(("JAVA_HOME", user));
        Assert.Equal(user, env.JavaHome);
        Assert.Equal(EngineSource.UserEnvironment, env.JavaSource);
        Assert.Equal(Path.Combine(user, "bin"), env.Variables["PATH"]); // no PATH at all → just the JDK bin
    }

    [Fact]
    public void Locator_PathAlreadyStartingWithJdkBin_IsNotDuplicated()
    {
        FakeJdk(AppJdk, "21.0.13");
        var path = Path.Combine(AppJdk, "bin") + @"\;C:\Windows";
        Assert.Equal(path, Resolve(("PATH", path)).Variables["PATH"]);
    }

    [Fact]
    public void Locator_JadxJar_UserFileWins_MissingUserFileFallsBackToApp()
    {
        var userJar = Path.Combine(_root, "my-jadx.jar");
        Touch(userJar);
        Touch(AppJar);

        var a = Resolve(("REA_JADX_MCP_JAR", userJar));
        Assert.Equal(userJar, a.JadxMcpJar);
        Assert.Equal(EngineSource.UserEnvironment, a.JadxMcpSource);

        var b = Resolve(("REA_JADX_MCP_JAR", Path.Combine(_root, "gone.jar")));
        Assert.Equal(AppJar, b.JadxMcpJar);
        Assert.Equal(EngineSource.AppInstalled, b.JadxMcpSource);
        Assert.Equal(AppJar, b.Variables["REA_JADX_MCP_JAR"]);
    }

    [Theory]
    [InlineData("21.0.13", 21)]
    [InlineData("\"17.0.12\"", 17)]
    [InlineData("1.8.0_392", 8)]
    [InlineData("25-ea", 25)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Locator_ParsesJavaReleaseVersion(string text, int? expected) => Assert.Equal(expected, EngineLocator.ParseJavaMajor(text));

    [Theory]
    [InlineData("12.1.4", true)]
    [InlineData("12.1", true)]
    [InlineData("12.1.5-DEV", true)]
    [InlineData("12.0.3", false)]
    [InlineData("11.4.2", false)]
    [InlineData("13.1.0", false)]
    [InlineData("12.10.0", false)]
    [InlineData(null, false)]
    public void Locator_AcceptsOnlyGhidra121Line(string? version, bool ok) => Assert.Equal(ok, EngineLocator.IsSupportedGhidraVersion(version));

    // ---------- installer ----------

    [Fact]
    public async Task Install_Zip_InstallsWritesMarker_AndLocatorFindsIt()
    {
        var body = JdkZip();
        var spec = Pinned(EngineCatalog.Jdk, body);
        var handler = FakeHandler.Serving(body);
        var inst = Installer(handler);
        var rec = new Recorder();
        Assert.False(inst.IsInstalled(spec));
        Assert.Equal(body.Length * 4L, inst.RequiredFreeBytesFor(new[] { spec }));

        await inst.InstallAsync(new[] { spec }, rec, CancellationToken.None);

        Assert.True(inst.IsInstalled(spec));
        Assert.True(File.Exists(Path.Combine(AppJdk, "bin", "java.exe")));
        var marker = JsonSerializer.Deserialize<EngineInstallMarker>(File.ReadAllText(inst.MarkerPath(spec)));
        Assert.NotNull(marker);
        Assert.Equal(spec.Sha256, marker.Sha256);
        Assert.Equal("21.0.13+9", marker.Version);
        AssertNoLeftovers();

        var phases = rec.Items;
        Assert.Contains(phases, p => p.Phase == EngineInstallPhase.Download && p.Done == body.Length && p.Total == body.Length);
        Assert.Contains(phases, p => p.Phase == EngineInstallPhase.Verify);
        Assert.Contains(phases, p => p.Phase == EngineInstallPhase.Extract);
        Assert.Equal(EngineInstallPhase.Installed, phases[^1].Phase);
        Assert.Equal(1, handler.Requests);

        var env = Resolve();
        Assert.Equal(AppJdk, env.JavaHome);
        Assert.Equal(EngineSource.AppInstalled, env.JavaSource);
        Assert.Equal(21, EngineLocator.ReadJavaMajor(AppJdk));
    }

    [Fact]
    public async Task Install_Ghidra_ChecksVersionInsideArchive()
    {
        byte[] GhidraZip(string version) => Zip(
            ("ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat", "@echo off"u8.ToArray()),
            ("ghidra_12.1.4_PUBLIC/Ghidra/application.properties", System.Text.Encoding.UTF8.GetBytes($"application.version={version}\n")));

        var wrong = GhidraZip("11.4.2");
        var wrongSpec = Pinned(EngineCatalog.Ghidra, wrong);
        var ex = await Assert.ThrowsAsync<EngineInstallException>(
            () => Installer(FakeHandler.Serving(wrong)).InstallAsync(new[] { wrongSpec }, null, CancellationToken.None));
        Assert.Equal(EngineInstallError.BadArchive, ex.Kind);
        Assert.False(Directory.Exists(AppGhidra));
        AssertNoLeftovers();

        var good = GhidraZip("12.1.4");
        var spec = Pinned(EngineCatalog.Ghidra, good);
        await Installer(FakeHandler.Serving(good)).InstallAsync(new[] { spec }, null, CancellationToken.None);
        Assert.Equal(AppGhidra, Resolve().GhidraDir);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_HashOrSizeMismatch_LeavesNothing(bool wrongSize)
    {
        var body = JdkZip();
        var spec = wrongSize ? Pinned(EngineCatalog.Jdk, body, size: body.Length + 1) : Pinned(EngineCatalog.Jdk, body, sha: new string('0', 64));
        var inst = Installer(FakeHandler.Serving(body));

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { spec }, null, CancellationToken.None));

        Assert.Equal(EngineInstallError.HashMismatch, ex.Kind);
        Assert.Same(spec, ex.Engine);
        Assert.False(Directory.Exists(AppJdk));
        Assert.False(File.Exists(inst.MarkerPath(spec)));
        AssertNoLeftovers();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("jdk-21.0.13+9/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("jdk-21.0.13+9/bin/java.exe:evil.txt")]
    [InlineData("jdk-21.0.13+9/.. /.. /evil.txt")]   // Windows trims the space: ".." again
    [InlineData("jdk-21.0.13+9/.../evil.txt")]       // trailing dots trimmed too
    [InlineData("jdk-21.0.13+9/bin./evil.txt")]
    [InlineData("jdk-21.0.13+9//evil.txt")]
    public async Task Install_UnsafeZipEntry_IsBadArchive_AndWritesNothing(string evil)
    {
        var body = Zip(("jdk-21.0.13+9/bin/java.exe", "MZ"u8.ToArray()), (evil, "pwned"u8.ToArray()));
        var spec = Pinned(EngineCatalog.Jdk, body);
        var inst = Installer(FakeHandler.Serving(body));

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { spec }, null, CancellationToken.None));

        Assert.Equal(EngineInstallError.BadArchive, ex.Kind);
        Assert.False(Directory.Exists(AppJdk));
        Assert.Empty(Directory.GetFiles(_root, "evil.txt", SearchOption.AllDirectories));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Install_CancelledMidDownload_ThrowsCanceled_AndLeavesNothing()
    {
        var body = Zip(("jdk-21.0.13+9/bin/java.exe", RandomNumberGenerator.GetBytes(256 * 1024)));
        var spec = Pinned(EngineCatalog.Jdk, body);
        using var cts = new CancellationTokenSource();
        var inst = Installer(FakeHandler.Streaming(() => new Trickle(body, 64 * 1024, cts.Cancel)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inst.InstallAsync(new[] { spec }, null, cts.Token));

        Assert.False(Directory.Exists(AppJdk));
        Assert.False(File.Exists(inst.MarkerPath(spec)));
        AssertNoLeftovers();
        Assert.Empty(Directory.GetFileSystemEntries(Engines));
    }

    [Fact]
    public async Task Install_ConnectionDropped_IsNetwork_AndLeavesNothing()
    {
        var body = JdkZip();
        var spec = Pinned(EngineCatalog.Jdk, body);
        var inst = Installer(FakeHandler.Streaming(() => new Trickle(body, 4096, () => throw new IOException("connection reset by peer"))));

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { spec }, null, CancellationToken.None));

        Assert.Equal(EngineInstallError.Network, ex.Kind);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Install_Http404_IsNetwork()
    {
        var spec = Pinned(EngineCatalog.Jdk, new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<EngineInstallException>(
            () => Installer(FakeHandler.Serving(Array.Empty<byte>(), HttpStatusCode.NotFound)).InstallAsync(new[] { spec }, null, CancellationToken.None));
        Assert.Equal(EngineInstallError.Network, ex.Kind);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Install_AlreadyInstalled_MakesNoRequest()
    {
        FakeJdk(AppJdk, "21.0.13");
        var spec = Pinned(EngineCatalog.Jdk, new byte[] { 1 });
        var handler = FakeHandler.Serving(new byte[] { 1 });
        var inst = Installer(handler);
        var rec = new Recorder();

        await inst.InstallAsync(new[] { spec }, rec, CancellationToken.None);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, inst.RequiredFreeBytesFor(new[] { spec }));
        Assert.Equal(EngineInstallPhase.Installed, Assert.Single(rec.Items).Phase);
        Assert.True(File.Exists(Path.Combine(AppJdk, "release"))); // the existing install was not replaced
    }

    [Fact]
    public async Task Install_ReplacesBrokenFolder()
    {
        Touch(Path.Combine(AppJdk, "half-copied.txt"));
        var body = JdkZip();
        var spec = Pinned(EngineCatalog.Jdk, body);
        var inst = Installer(FakeHandler.Serving(body));

        await inst.InstallAsync(new[] { spec }, null, CancellationToken.None);

        Assert.True(inst.IsInstalled(spec));
        Assert.False(File.Exists(Path.Combine(AppJdk, "half-copied.txt")));
    }

    [Fact]
    public async Task Install_NotEnoughSpace_IsNoSpace_BeforeAnyRequest()
    {
        var body = JdkZip();
        var spec = Pinned(EngineCatalog.Jdk, body);
        var handler = FakeHandler.Serving(body);

        var ex = await Assert.ThrowsAsync<EngineInstallException>(
            () => Installer(handler, _ => 10).InstallAsync(new[] { spec }, null, CancellationToken.None));

        Assert.Equal(EngineInstallError.NoSpace, ex.Kind);
        Assert.Equal(body.Length * 4L, ex.RequiredBytes);
        Assert.Equal(10, ex.AvailableBytes);
        Assert.Equal(0, handler.Requests);
        AssertNoLeftovers();
    }

    [Fact]
    public void Installer_DefaultFreeSpaceProbe_ReadsTheDrive() => Assert.True(new EngineInstaller(_root).AvailableFreeBytes > 0);

    [Fact]
    public async Task Install_WhileAnotherInstallRuns_IsBusy()
    {
        var body = JdkZip();
        var spec = Pinned(EngineCatalog.Jdk, body);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inst = Installer(new FakeHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Ok(body);
        }));

        var first = inst.InstallAsync(new[] { spec }, null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { spec }, null, CancellationToken.None));
        Assert.Equal(EngineInstallError.Busy, ex.Kind);

        release.SetResult();
        await first;
        Assert.True(inst.IsInstalled(spec));
    }

    [Fact]
    public async Task Install_SingleFile_PlacesJar_AndLocatorPicksItUp()
    {
        var jar = RandomNumberGenerator.GetBytes(100_000);
        var spec = Pinned(EngineCatalog.JadxMcp, jar);
        var inst = Installer(FakeHandler.Serving(jar));

        await inst.InstallAsync(new[] { spec }, null, CancellationToken.None);

        Assert.Equal(jar, File.ReadAllBytes(AppJar));
        Assert.True(inst.IsInstalled(spec));
        Assert.True(File.Exists(inst.MarkerPath(spec)));
        AssertNoLeftovers();

        var env = Resolve();
        Assert.Equal(AppJar, env.JadxMcpJar);
        Assert.Equal(EngineSource.AppInstalled, env.JadxMcpSource);
        Assert.Equal(AppJar, env.Variables["REA_JADX_MCP_JAR"]);
    }

    [Fact]
    public async Task Install_SingleFile_HashMismatch_LeavesNothing()
    {
        var jar = RandomNumberGenerator.GetBytes(100_000);
        var spec = Pinned(EngineCatalog.JadxMcp, jar, sha: Sha(new byte[] { 1, 2, 3 }));
        var inst = Installer(FakeHandler.Serving(jar));

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { spec }, null, CancellationToken.None));

        Assert.Equal(EngineInstallError.HashMismatch, ex.Kind);
        Assert.False(Directory.Exists(Path.Combine(Engines, spec.FolderName)));
        Assert.Null(Resolve().JadxMcpJar);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Install_FailureOfOneEngine_KeepsTheOnesBefore()
    {
        var jdk = JdkZip();
        var jar = RandomNumberGenerator.GetBytes(10_000);
        var jdkSpec = Pinned(EngineCatalog.Jdk, jdk);
        var jarSpec = Pinned(EngineCatalog.JadxMcp, jar, sha: new string('0', 64));
        var inst = Installer(new FakeHandler((req, _) =>
            Task.FromResult(Ok(req.RequestUri?.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) == true ? jdk : jar))));

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => inst.InstallAsync(new[] { jdkSpec, jarSpec }, null, CancellationToken.None));

        Assert.Same(jarSpec, ex.Engine);
        Assert.True(inst.IsInstalled(jdkSpec));
        Assert.False(inst.IsInstalled(jarSpec));
        AssertNoLeftovers();
    }

    // ---------- launcher ----------

    [Fact]
    public void Launcher_AppliesEngineEnvironment_AndKeepsNoColor()
    {
        FakeGhidra(AppGhidra, "12.1.4");
        FakeJdk(AppJdk, "21.0.13");
        Touch(AppJar);
        var env = Resolve(("PATH", @"C:\Windows\system32"));

        var psi = ReaLauncher.Create(new NodeInfo(@"C:\nodejs\node.exe", new Version(24, 11, 1)), new[] { "mcp" }, null, env);

        Assert.Equal(AppGhidra, psi.Environment["GHIDRA_INSTALL_DIR"]);
        Assert.Equal(AppJdk, psi.Environment["JAVA_HOME"]);
        Assert.Equal(Path.Combine(AppJdk, "bin") + Path.PathSeparator + @"C:\Windows\system32", psi.Environment["PATH"]);
        Assert.Equal(AppJar, psi.Environment["REA_JADX_MCP_JAR"]);
        Assert.Equal("1", psi.Environment["NO_COLOR"]);
        Assert.Equal("mcp", psi.ArgumentList[^1]);
    }

    [Fact]
    public void Launcher_NoEngines_LeavesInheritedEnvironmentAlone()
    {
        var inherited = Environment.GetEnvironmentVariable("PATH");
        var psi = ReaLauncher.Create(new NodeInfo(@"C:\nodejs\node.exe", new Version(24, 11, 1)), new[] { "mcp" }, null, EngineEnvironment.None);
        Assert.Equal(inherited, psi.Environment["PATH"]);
        Assert.Equal("1", psi.Environment["NO_COLOR"]);
    }

    // ---------- tool rows ----------

    private static readonly NodeInfo Node24 = new("node.exe", new Version(24, 11, 1));

    private static ReaDoctorReport Doctor() => new(false, false, "6.4.0", 170, 20, Array.Empty<ReaDoctorCheck>(), default);

    private static ReaCapabilityReport GhidraProvider(string status) =>
        new(Array.Empty<ReaCapability>(), new[] { new ReaProviderCandidate("ghidra", "Ghidra", status, "reason") }, default);

    private static EngineEnvironment Engines3(string? ghidra, string? java, string? jar) => new(
        ghidra, ghidra is null ? EngineSource.None : EngineSource.AppInstalled,
        java, java is null ? EngineSource.None : EngineSource.AppInstalled,
        jar, jar is null ? EngineSource.None : EngineSource.AppInstalled,
        new Dictionary<string, string>());

    private static ToolStatus Row(ToolProbeInputs i, string id) => ToolStatusMapper.Map(i).Single(s => s.Id == id);

    [Fact]
    public void Rows_GhidraAvailable_IsOk()
    {
        var i = new ToolProbeInputs(Node24, true, Doctor(), GhidraProvider("available"), null, 21, false, null, Engines3(@"C:\g", @"C:\j", null));
        Assert.Equal("ghidra.ok", Row(i, "ghidra").MessageCode);
    }

    [Fact]
    public void Rows_GhidraHandedToReaButRefused_IsRejected_NotNotSet()
    {
        var i = new ToolProbeInputs(Node24, true, Doctor(), GhidraProvider("unavailable"), null, 21, false, null, Engines3(@"C:\g", @"C:\j", null));
        Assert.Equal("ghidra.rejected", Row(i, "ghidra").MessageCode);
        var none = i with { Engines = Engines3(null, @"C:\j", null) };
        Assert.Equal("ghidra.notSet", Row(none, "ghidra").MessageCode);
    }

    [Theory]
    [InlineData(21, "jadx.ok", ToolLevel.Ok)]
    [InlineData(17, "jadx.ok", ToolLevel.Ok)]
    [InlineData(11, "jadx.noJdk", ToolLevel.Warn)]
    [InlineData(null, "jadx.noJdk", ToolLevel.Warn)]
    public void Rows_JadxJar_NeedsJdk17(int? java, string code, ToolLevel level)
    {
        var i = new ToolProbeInputs(Node24, true, Doctor(), GhidraProvider("unavailable"), null, java, false, null, Engines3(null, null, @"C:\x.jar"));
        var row = Row(i, "jadx");
        Assert.Equal(code, row.MessageCode);
        Assert.Equal(level, row.Level);
    }

    [Fact]
    public void Rows_NoJarAndNoCli_IsMissing()
    {
        var i = new ToolProbeInputs(Node24, true, Doctor(), null, null, 21, false, null, EngineEnvironment.None);
        Assert.Equal("jadx.missing", Row(i, "jadx").MessageCode);
    }

    [Fact]
    public void Rows_JadxCliWithoutJar_IsNotReady()
    {
        var i = new ToolProbeInputs(Node24, true, Doctor(), null, null, 21, true, null, EngineEnvironment.None);
        var row = Row(i, "jadx");
        Assert.Equal("jadx.cliOnly", row.MessageCode);
        Assert.Equal(ToolLevel.Warn, row.Level);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(81_369_088L, "77.6 MB")]
    [InlineData(569_376_768L, "543 MB")]
    [InlineData(3_113_851_290L, "2.9 GB")]
    public void ByteSize_KeepsNumberAndUnitOnOneLine(long bytes, string expected)
    {
        var s = ReverseX.Core.Files.ByteSize.Format(bytes);
        Assert.Equal(expected, s);
        Assert.False(s.Contains(' '));
    }
}
