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
}
