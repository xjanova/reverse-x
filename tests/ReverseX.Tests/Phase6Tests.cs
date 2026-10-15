using System.IO.Compression;
using System.Net;
using ReverseX.Core.Download;
using Xunit;

namespace ReverseX.Tests;

public class Phase6Tests
{
    [Theory]
    [InlineData("https://play.google.com/store/apps/details?id=com.whatsapp", "com.whatsapp")]
    [InlineData("https://play.google.com/store/apps/details?id=com.whatsapp&hl=th&gl=TH", "com.whatsapp")]
    [InlineData("  https://play.google.com/store/apps/details?hl=en&id=org.mozilla.firefox ", "org.mozilla.firefox")]
    [InlineData("market://details?id=com.a.b", "com.a.b")]
    [InlineData("com.example.app", "com.example.app")]
    [InlineData("https://evil.example/store/apps/details?id=com.whatsapp", null)]
    [InlineData("https://play.google.com/store/apps/details?id=../../x", null)]
    [InlineData("https://play.google.com/store", null)]
    [InlineData("hello", null)]
    [InlineData("", null)]
    public void PlayLink_ParsesPackageId(string input, string? expected) => Assert.Equal(expected, PlayLink.TryParse(input));

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static async Task<FetchedApp> Run(byte[] body, string contentType, int status = 200)
    {
        var port = new Random().Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var l = new HttpListener();
        l.Prefixes.Add(prefix);
        l.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await l.GetContextAsync();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });
        var root = Path.Combine(Path.GetTempPath(), "rx-p6-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new ApkFetcher(root, _ => prefix + "x");
            return await f.FetchAsync("com.test.app", null, CancellationToken.None);
        }
        finally
        {
            await server;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Fetch_Apk_IsKeptAndUnpacked()
    {
        var apk = Zip(("AndroidManifest.xml", new byte[] { 1 }), ("classes.dex", new byte[] { 2 }));
        var root = Path.Combine(Path.GetTempPath(), "rx-p6-" + Guid.NewGuid().ToString("N"));
        var port = new Random().Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var l = new HttpListener();
        l.Prefixes.Add(prefix);
        l.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await l.GetContextAsync();
            ctx.Response.ContentType = "application/vnd.android.package-archive";
            await ctx.Response.OutputStream.WriteAsync(apk);
            ctx.Response.Close();
        });
        try
        {
            var r = await new ApkFetcher(root, _ => prefix + "x").FetchAsync("com.test.app", null, CancellationToken.None);
            Assert.True(File.Exists(r.ApkPath));
            Assert.True(File.Exists(Path.Combine(r.UnpackedDir, "classes.dex")));
        }
        finally
        {
            await server;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Fetch_Xapk_UsesBaseApk()
    {
        var inner = Zip(("AndroidManifest.xml", new byte[] { 1 }), ("classes.dex", new byte[] { 2 }));
        var xapk = Zip(("manifest.json", new byte[] { 3 }), ("com.test.app.apk", inner));
        var r = await Run(xapk, "application/octet-stream");
        Assert.EndsWith("com.test.app-unpacked", r.UnpackedDir);
    }

    [Fact]
    public async Task Fetch_Html_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ApkFetchException>(() => Run("<html/>"u8.ToArray(), "text/html"));
        Assert.Equal(ApkFetchError.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Fetch_NonZip_IsNotApk()
    {
        var ex = await Assert.ThrowsAsync<ApkFetchException>(() => Run(new byte[] { 1, 2, 3, 4 }, "application/octet-stream"));
        Assert.Equal(ApkFetchError.NotApk, ex.Kind);
    }

    // ---------- limits, cancel, cleanup ----------

    /// <summary>Serves each request with the next body/status; tolerant of the client hanging up early.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _l = new();
        private readonly Task _loop;
        public string Prefix { get; }

        public Server(params (byte[] Body, int Status)[] replies)
        {
            var port = new Random().Next(20000, 40000);
            Prefix = $"http://127.0.0.1:{port}/";
            _l.Prefixes.Add(Prefix);
            _l.Start();
            _loop = Task.Run(async () =>
            {
                foreach (var (body, status) in replies)
                {
                    try
                    {
                        var ctx = await _l.GetContextAsync();
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException) { }
                }
            });
        }

        public void Dispose()
        {
            try { _l.Stop(); _l.Close(); } catch (ObjectDisposedException) { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "rx-p6-" + Guid.NewGuid().ToString("N"));
    private static void Clean(string root) { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static byte[] ValidApk() => Zip(("AndroidManifest.xml", new byte[] { 1 }), ("classes.dex", new byte[] { 2 }));

    [Fact]
    public async Task Fetch_DownloadOverLimit_IsTooLarge_AndLeavesNothing()
    {
        var root = TempRoot();
        using var s = new Server((new byte[4096], 200));
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x", maxDownloadBytes: 1024);
            var ex = await Assert.ThrowsAsync<ApkFetchException>(() => f.FetchAsync("com.test.app", null, CancellationToken.None));
            Assert.Equal(ApkFetchError.TooLarge, ex.Kind);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "com.test.app")));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task Fetch_ZipBomb_IsTooLarge_AndUnpackedFolderRemoved()
    {
        // ~1 MB of zeros compresses to about 1 KB; the unpack budget is 64 KB.
        var bomb = Zip(("AndroidManifest.xml", new byte[] { 1 }), ("assets/zeros.bin", new byte[1024 * 1024]));
        Assert.True(bomb.Length < 64 * 1024);
        var root = TempRoot();
        using var s = new Server((bomb, 200));
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x", maxUnpackedBytes: 64 * 1024);
            var ex = await Assert.ThrowsAsync<ApkFetchException>(() => f.FetchAsync("com.test.app", null, CancellationToken.None));
            Assert.Equal(ApkFetchError.TooLarge, ex.Kind);
            var dir = Path.Combine(root, "com.test.app");
            Assert.False(Directory.Exists(Path.Combine(dir, "com.test.app-unpacked")));
            Assert.False(File.Exists(Path.Combine(dir, "com.test.app.apk")));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task Fetch_XapkWithOversizedBaseApk_IsTooLarge()
    {
        // Inner APK stored uncompressed (~256 KB); the outer bundle deflates it to a few KB.
        byte[] inner;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var m = z.CreateEntry("AndroidManifest.xml", CompressionLevel.NoCompression).Open()) m.WriteByte(1);
                using (var d = z.CreateEntry("classes.dex", CompressionLevel.NoCompression).Open()) d.Write(new byte[256 * 1024]);
            }
            inner = ms.ToArray();
        }
        var xapk = Zip(("manifest.json", new byte[] { 3 }), ("com.test.app.apk", inner));
        Assert.True(xapk.Length < 64 * 1024 && inner.Length > 64 * 1024);
        var root = TempRoot();
        using var s = new Server((xapk, 200));
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x", maxDownloadBytes: 64 * 1024);
            var ex = await Assert.ThrowsAsync<ApkFetchException>(() => f.FetchAsync("com.test.app", null, CancellationToken.None));
            Assert.Equal(ApkFetchError.TooLarge, ex.Kind);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task Fetch_TooManyEntries_IsTooLarge()
    {
        var entries = Enumerable.Range(0, 50).Select(i => ($"res/f{i}.xml", new byte[] { 1 })).Prepend(("AndroidManifest.xml", new byte[] { 1 })).ToArray();
        var root = TempRoot();
        using var s = new Server((Zip(entries), 200));
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x", maxEntries: 10);
            var ex = await Assert.ThrowsAsync<ApkFetchException>(() => f.FetchAsync("com.test.app", null, CancellationToken.None));
            Assert.Equal(ApkFetchError.TooLarge, ex.Kind);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task Fetch_Cancelled_ThrowsOperationCanceled_NotNetworkError()
    {
        var root = TempRoot();
        using var s = new Server((ValidApk(), 200));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.FetchAsync("com.test.app", null, cts.Token));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task Fetch_FailedRetry_KeepsPreviousDownload()
    {
        var root = TempRoot();
        using var s = new Server((ValidApk(), 200), (Array.Empty<byte>(), 404));
        try
        {
            var f = new ApkFetcher(root, _ => s.Prefix + "x");
            var first = await f.FetchAsync("com.test.app", null, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<ApkFetchException>(() => f.FetchAsync("com.test.app", null, CancellationToken.None));
            Assert.Equal(ApkFetchError.NotFound, ex.Kind);
            Assert.True(File.Exists(first.ApkPath));
            Assert.True(File.Exists(Path.Combine(first.UnpackedDir, "classes.dex")));
        }
        finally { Clean(root); }
    }
}
