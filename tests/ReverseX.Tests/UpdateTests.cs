using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReverseX.Licensing;
using Xunit;

namespace ReverseX.Tests;

public sealed class ReleaseZipFactAttribute : FactAttribute
{
    public ReleaseZipFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REVERSEX_RELEASE_ZIP") is not { Length: > 0 } p || !System.IO.File.Exists(p))
            Skip = "REVERSEX_RELEASE_ZIP is not set — run deploy/publish.ps1 and point it at the zip to check a real signed release";
    }
}

/// <summary>Signed self-update against a fake xman API and a fake download handler — no network.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-upd-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string Url = "https://xman4289.com/reverse-x/download/1.1.0";

    public UpdateTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(InstallDir);
        System.IO.File.WriteAllText(Path.Combine(InstallDir, "ReverseX.exe"), "old exe");
    }

    public void Dispose()
    {
        _releaseKey.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string DataDir => Path.Combine(_dir, "data");
    private string InstallDir => Path.Combine(_dir, "install");
    private byte[] PublicKey => _releaseKey.ExportSubjectPublicKeyInfo();

    // ---------------------------------------------------------------- package helpers

    /// <summary>A published folder signed like tools/ReleaseSigner does.</summary>
    private string SignedFolder(string version = "1.1.0", ECDsa? key = null, string product = "reverse-x")
    {
        var folder = Path.Combine(_dir, "pkg-" + Guid.NewGuid().ToString("N")[..6], "ReverseX");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        System.IO.File.WriteAllText(Path.Combine(folder, "ReverseX.exe"), "new exe " + version);
        System.IO.File.WriteAllText(Path.Combine(folder, "ReverseX.Core.dll"), "core");
        System.IO.File.WriteAllText(Path.Combine(folder, "Assets", "logo.png"), "png");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(p => new
        {
            path = Path.GetRelativePath(folder, p).Replace('\\', '/'),
            size = new FileInfo(p).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(p))).ToLowerInvariant(),
        }).ToList();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { product, version, files }, new JsonSerializerOptions { WriteIndented = true });
        var sig = (key ?? _releaseKey).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        System.IO.File.WriteAllBytes(Path.Combine(folder, UpdatePackageVerifier.ManifestFileName), manifest);
        System.IO.File.WriteAllText(Path.Combine(folder, UpdatePackageVerifier.SignatureFileName), Convert.ToBase64String(sig));
        return folder;
    }

    private static byte[] Zip(string folder)
    {
        var zip = folder + ".zip";
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return System.IO.File.ReadAllBytes(zip);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static LicenseTests.FakeXman Announce(byte[] package, string url = Url, string version = "1.1.0", string? sha = null, long? size = null)
    {
        var x = new LicenseTests.FakeXman();
        x.Routes["/update/check"] = () => LicenseTests.Reply(200, JsonSerializer.Serialize(new
        {
            success = true, has_update = true, latest_version = version, download_url = url,
            sha256 = sha ?? Sha(package), file_size = size ?? package.LongLength,
        }));
        return x;
    }

    /// <summary>Serves bytes like xman's download route; <c>cut</c> stops early (no Content-Length), <c>drop</c> breaks the connection.</summary>
    private sealed class FakeDownload(byte[] body, int? cut = null, bool drop = false, HttpStatusCode status = HttpStatusCode.OK, string media = "application/octet-stream") : HttpMessageHandler
    {
        public readonly List<Uri> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var r = new HttpResponseMessage(status);
            if ((int)status is >= 300 and < 400)
            {
                r.Headers.Location = new Uri("https://objects.example.com/ReverseX.zip");
                return Task.FromResult(r);
            }
            if (cut is null && !drop)
            {
                r.Content = new ByteArrayContent(body);
            }
            else
            {
                r.Content = new StreamContent(new Trickle(body, cut ?? body.Length / 2, drop));
            }
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
            return Task.FromResult(r);
        }
    }

    /// <summary>Non-seekable (so no Content-Length): gives <c>until</c> bytes, then ends or throws.</summary>
    private sealed class Trickle(byte[] data, int until, bool drop) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= until)
            {
                if (drop) throw new IOException("connection reset by peer");
                return 0;
            }
            var n = Math.Min(count, until - _pos);
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

    private (UpdateService Svc, List<ProcessStartInfo> Launched) Service(LicenseTests.FakeXman api, HttpMessageHandler download)
    {
        var launched = new List<ProcessStartInfo>();
        var svc = new UpdateService(api, DataDir, InstallDir, "1.0.0", _ => { })
        {
            DownloadClient = () => new HttpClient(download, disposeHandler: false),
            ReleaseKeyOverride = PublicKey,
            Launch = launched.Add,
        };
        return (svc, launched);
    }

    private bool StagingEmpty() =>
        !Directory.Exists(Path.Combine(DataDir, "update")) || !Directory.EnumerateDirectories(Path.Combine(DataDir, "update")).Any();

    // ---------------------------------------------------------------- tests

    [Fact]
    public async Task SignedPackage_IsVerified_AndHandedToTheNewUpdater()
    {
        var zip = Zip(SignedFolder());
        var dl = new FakeDownload(zip);
        var (svc, launched) = Service(Announce(zip), dl);

        Assert.True((await svc.CheckAsync()).Ok);
        Assert.True(svc.Status.Available);
        Assert.Equal("1.1.0", svc.Status.Latest);
        var r = await svc.InstallAsync();

        Assert.True(r.Ok, $"{r.Error} {r.Detail}");
        Assert.Equal(new Uri(Url), Assert.Single(dl.Requests));
        var psi = Assert.Single(launched);
        Assert.EndsWith(Path.Combine("ReverseX", "ReverseX.exe"), psi.FileName);
        Assert.Equal(Updater.Command, psi.ArgumentList[0]);
        Assert.Equal(InstallDir, psi.ArgumentList[2]);
        Assert.Equal("1.1.0", psi.ArgumentList[3]);
        Assert.Equal(Environment.ProcessId.ToString(), psi.ArgumentList[4]);
        Assert.Equal(UpdateState.Installing, svc.Status.State);
    }

    [Fact]
    public async Task WrongSignature_IsNotInstalled()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Zip(SignedFolder(key: stranger));
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        var r = await svc.InstallAsync();
        Assert.Equal(UpdateError.BadSignature, r.Error);
        Assert.Empty(launched);
        Assert.Equal(UpdateState.Failed, svc.Status.State);
        Assert.True(StagingEmpty());
        Assert.Equal("old exe", System.IO.File.ReadAllText(Path.Combine(InstallDir, "ReverseX.exe")));
    }

    [Fact]
    public async Task FileChangedAfterSigning_IsNotInstalled()
    {
        var folder = SignedFolder();
        System.IO.File.WriteAllText(Path.Combine(folder, "ReverseX.Core.dll"), "evil");
        var zip = Zip(folder);
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.FilesMismatch, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task ExtraUnsignedDll_IsNotInstalled()
    {
        var folder = SignedFolder();
        System.IO.File.WriteAllText(Path.Combine(folder, "version.dll"), "planted");
        var zip = Zip(folder);
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.FilesMismatch, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task UnsignedPackage_IsNotInstalled()
    {
        var folder = SignedFolder();
        System.IO.File.Delete(Path.Combine(folder, UpdatePackageVerifier.SignatureFileName));
        var zip = Zip(folder);
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.Unsigned, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task SignedForAnotherVersionOrProduct_IsNotInstalled()
    {
        var zip = Zip(SignedFolder(version: "1.0.5"));
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.WrongVersion, (await svc.InstallAsync()).Error);

        zip = Zip(SignedFolder(product: "anti-x"));
        (svc, launched) = Service(Announce(zip), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.WrongVersion, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task IncompleteDownload_IsNotInstalled()
    {
        var zip = Zip(SignedFolder());
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip, cut: zip.Length - 100));
        await svc.CheckAsync();
        var r = await svc.InstallAsync();
        Assert.Equal(UpdateError.Incomplete, r.Error);
        Assert.Empty(launched);
        Assert.True(StagingEmpty());
    }

    [Fact]
    public async Task ConnectionDroppedMidDownload_IsNotInstalled()
    {
        var zip = Zip(SignedFolder());
        var (svc, launched) = Service(Announce(zip), new FakeDownload(zip, drop: true));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.Incomplete, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
        Assert.True(StagingEmpty());
    }

    [Fact]
    public async Task ChecksumOrSizeMismatch_IsNotInstalled()
    {
        var zip = Zip(SignedFolder());
        var (svc, launched) = Service(Announce(zip, sha: new string('0', 64)), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.HashMismatch, (await svc.InstallAsync()).Error);

        (svc, launched) = Service(Announce(zip, size: zip.Length + 10), new FakeDownload(zip));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.SizeMismatch, (await svc.InstallAsync()).Error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task DownloadOutsideXman_IsRefused_BeforeAnyDownload()
    {
        var zip = Zip(SignedFolder());
        var dl = new FakeDownload(zip);
        var (svc, _) = Service(Announce(zip, url: "https://github.com/xjanova/reverse-x/releases/download/v1.1.0/ReverseX-1.1.0-win-x64.zip"), dl);
        var r = await svc.CheckAsync();
        Assert.Equal(UpdateError.NotXmanUrl, r.Error);
        Assert.False(svc.Status.Available);
        Assert.Equal(UpdateError.NothingToInstall, (await svc.InstallAsync()).Error);
        Assert.Empty(dl.Requests);
    }

    [Fact]
    public async Task Redirect_HtmlPage_AndHttpErrors_AreRefused()
    {
        var zip = Zip(SignedFolder());
        var (svc, _) = Service(Announce(zip), new FakeDownload(zip, status: HttpStatusCode.Found));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.Redirected, (await svc.InstallAsync()).Error);

        (svc, _) = Service(Announce(zip), new FakeDownload(zip, media: "text/html"));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.NotAPackage, (await svc.InstallAsync()).Error);

        (svc, _) = Service(Announce(zip), new FakeDownload(zip, status: HttpStatusCode.BadGateway));
        await svc.CheckAsync();
        Assert.Equal(UpdateError.HttpError, (await svc.InstallAsync()).Error);
    }

    [Fact]
    public async Task Check_Outage_IsJustOffline()
    {
        var api = new LicenseTests.FakeXman { Outage = () => LicenseTests.Reply(503, null) };
        var (svc, _) = Service(api, new FakeDownload(Array.Empty<byte>()));
        var r = await svc.CheckAsync();
        Assert.Equal(UpdateError.Offline, r.Error);
        Assert.False(svc.Status.Available);
        Assert.Equal(UpdateState.Idle, svc.Status.State);
    }

    [Fact]
    public async Task Check_SameOrOlderVersion_IsUpToDate()
    {
        var zip = Zip(SignedFolder(version: "0.9.0"));
        var (svc, _) = Service(Announce(zip, version: "0.9.0"), new FakeDownload(zip));
        Assert.True((await svc.CheckAsync()).Ok);
        Assert.False(svc.Status.Available);
    }

    [Fact]
    public void Updater_CopiesNewFiles_KeepsOtherFiles_AndStartsTheApp()
    {
        var source = SignedFolder();
        System.IO.File.WriteAllText(Path.Combine(InstallDir, "my-notes.txt"), "user file");
        var started = new List<ProcessStartInfo>();

        var code = Updater.Run(new[] { Updater.Command, source, InstallDir, "1.1.0", "0", DataDir }, started.Add);

        Assert.Equal(0, code);
        Assert.Equal("new exe 1.1.0", System.IO.File.ReadAllText(Path.Combine(InstallDir, "ReverseX.exe")));
        Assert.Equal("png", System.IO.File.ReadAllText(Path.Combine(InstallDir, "Assets", "logo.png")));
        Assert.Equal("user file", System.IO.File.ReadAllText(Path.Combine(InstallDir, "my-notes.txt")));
        Assert.Equal(Path.Combine(InstallDir, "ReverseX.exe"), Assert.Single(started).FileName);
        var outcome = Updater.TakeOutcome(DataDir);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Ok);
        Assert.Null(Updater.TakeOutcome(DataDir));
    }

    [Fact]
    public void Updater_OldAppStillRunning_TouchesNothing()
    {
        var source = SignedFolder();
        using var self = Process.GetCurrentProcess(); // never exits during the test
        var code = Updater.Run(new[] { Updater.Command, source, InstallDir, "1.1.0", self.Id.ToString(), DataDir }, _ => { }, TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, code);
        Assert.Equal("old exe", System.IO.File.ReadAllText(Path.Combine(InstallDir, "ReverseX.exe")));
        Assert.False(Updater.TakeOutcome(DataDir)!.Ok);
    }

    [Fact]
    public void Updater_BackupHoldsOnlyReplacedFiles()
    {
        var source = SignedFolder();
        System.IO.File.WriteAllText(Path.Combine(InstallDir, "big-unrelated.bin"), "x");
        var backup = Path.Combine(_dir, "backup");
        var added = Updater.BackupReplaced(source, InstallDir, backup);
        Assert.Equal("old exe", System.IO.File.ReadAllText(Path.Combine(backup, "ReverseX.exe")));
        Assert.False(System.IO.File.Exists(Path.Combine(backup, "big-unrelated.bin")));
        Assert.Contains(Path.Combine(InstallDir, "ReverseX.Core.dll"), added);
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0-beta", true)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("garbage", "1.0.0", false)]
    public void Version_IsNewer(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdatePackageVerifier.IsNewer(candidate, current));

    [Fact]
    public void CompiledReleaseKey_IsAValidP256PublicKey_AndRejectsOtherSigners()
    {
        var folder = SignedFolder(); // signed with the test key, not the release key
        Assert.Equal(UpdatePackageVerifier.Result.BadSignature, UpdatePackageVerifier.Verify(folder, "1.1.0", "1.0.0"));
        Assert.Equal(UpdatePackageVerifier.Result.Valid, UpdatePackageVerifier.Verify(folder, "1.1.0", "1.0.0", PublicKey));
    }

    /// <summary>
    /// The zip deploy/publish.ps1 really produced (REVERSEX_RELEASE_ZIP=path), through the app's own pipeline with the
    /// public key compiled into Reverse X: proves the signing secret and the embedded key belong together.
    /// </summary>
    [ReleaseZipFact]
    public async Task RealReleaseZip_PassesTheAppsOwnPipeline_WithTheCompiledKey()
    {
        var path = Environment.GetEnvironmentVariable("REVERSEX_RELEASE_ZIP")!;
        var zip = System.IO.File.ReadAllBytes(path);
        var version = Path.GetFileName(path).Split('-')[1];
        var launched = new List<ProcessStartInfo>();
        var svc = new UpdateService(Announce(zip, version: version), DataDir, InstallDir, "0.0.1", _ => { })
        {
            DownloadClient = () => new HttpClient(new FakeDownload(zip), disposeHandler: true),
            Launch = launched.Add, // ReleaseKeyOverride not set → the real public key
        };
        await svc.CheckAsync();
        var r = await svc.InstallAsync();
        Assert.True(r.Ok, $"{r.Error} {r.Detail}");
        Assert.Single(launched);

        // and the detached .sig of the whole zip
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePackageVerifier.ReleasePublicKey), out _);
        var sig = Convert.FromBase64String(System.IO.File.ReadAllText(path + ".sig").Trim());
        Assert.True(pub.VerifyData(zip, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Repository_HoldsNoPrivateSigningKey()
    {
        // walk up to the repo root and make sure nothing that looks like a PKCS#8 EC private key is committed
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "ReverseX.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        foreach (var f in Directory.EnumerateFiles(dir!.FullName, "*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}")) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".cs" or ".txt" or ".json" or ".yml" or ".yaml" or ".ps1" or ".md" or ".xml" or ".props" or ".csproj" or ".key" or ".pem")) continue;
            var text = System.IO.File.ReadAllText(f);
            // needles are split so this file does not match itself
            Assert.DoesNotContain("MIGHAgEAMBMG" + "ByqGSM49AgEG", text); // base64 PKCS#8 P-256 private key prefix
            Assert.DoesNotContain("BEGIN " + "PRIVATE KEY", text);
            Assert.DoesNotContain("BEGIN EC " + "PRIVATE KEY", text);
        }
    }
}
