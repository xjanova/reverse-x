using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace ReverseX.Core.Download;

public enum ApkFetchError { NotFound, Blocked, Network, NotApk, TooLarge }

public sealed class ApkFetchException : Exception
{
    public ApkFetchError Kind { get; }
    public ApkFetchException(ApkFetchError kind, Exception? inner = null) : base(kind.ToString(), inner) => Kind = kind;
}

public readonly record struct FetchProgress(long Received, long? Total);

/// <param name="ApkPath">The downloaded (and, for XAPK bundles, the inner base) APK.</param>
/// <param name="UnpackedDir">Folder holding the extracted APK contents.</param>
public sealed record FetchedApp(string PackageId, string ApkPath, string UnpackedDir);

public interface IApkFetcher
{
    Task<FetchedApp> FetchAsync(string packageId, IProgress<FetchProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Google Play offers no direct download, so the APK comes from the APKPure mirror (public builds only —
/// no paid, region-locked or Play-Integrity-gated apps). The file is validated as a ZIP, XAPK bundles are
/// reduced to their base APK, then everything is unpacked next to it.
/// </summary>
public sealed class ApkFetcher : IApkFetcher
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    public const long DefaultMaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultMaxUnpackedBytes = 4L * 1024 * 1024 * 1024;
    public const int DefaultMaxEntries = 200_000;

    private readonly string _root;
    private readonly Func<string, string> _urlFor;
    private readonly long _maxDownload;
    private readonly long _maxUnpacked;
    private readonly int _maxEntries;

    public ApkFetcher(string? root = null, Func<string, string>? urlFor = null,
        long maxDownloadBytes = DefaultMaxDownloadBytes, long maxUnpackedBytes = DefaultMaxUnpackedBytes, int maxEntries = DefaultMaxEntries)
    {
        _root = root ?? AppPaths.DownloadsDir;
        _maxDownload = maxDownloadBytes;
        _maxUnpacked = maxUnpackedBytes;
        _maxEntries = maxEntries;
        _urlFor = urlFor ?? (pkg => $"https://d.apkpure.net/b/APK/{Uri.EscapeDataString(pkg)}?version=latest");
    }

    public async Task<FetchedApp> FetchAsync(string packageId, IProgress<FetchProgress>? progress, CancellationToken ct)
    {
        if (!PlayLink.IsValidPackageId(packageId)) throw new ArgumentException("Bad package id", nameof(packageId));

        var dir = Path.Combine(_root, packageId);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, packageId + ".download");
        var apk = Path.Combine(dir, packageId + ".apk");
        var unpacked = Path.Combine(dir, packageId + "-unpacked");
        var ok = false;
        var replacing = false;

        try
        {
            await DownloadAsync(_urlFor(packageId), part, _maxDownload, progress, ct).ConfigureAwait(false);
            if (!HasZipMagic(part)) throw new ApkFetchException(ApkFetchError.NotApk);

            replacing = true;
            if (File.Exists(apk)) File.Delete(apk);
            if (HasEntry(part, "AndroidManifest.xml"))
            {
                File.Move(part, apk);
            }
            else
            {
                ExtractBaseApk(part, apk, packageId, ct);
                File.Delete(part);
            }

            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            SafeExtract(apk, unpacked, ct);
            ok = true;
            return new FetchedApp(packageId, apk, unpacked);
        }
        catch (InvalidDataException ex)
        {
            throw new ApkFetchException(ApkFetchError.NotApk, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ApkFetchException(ApkFetchError.Network, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Stalled connection: the inactivity timer fired, not the caller.
            throw new ApkFetchException(ApkFetchError.Network, ex);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            if (replacing && !ok)
            {
                // Cancelled or rejected half-way: leave no partial APK or half-unpacked folder behind.
                try { if (File.Exists(apk)) File.Delete(apk); } catch (IOException) { }
                try { if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true); } catch (IOException) { }
            }
        }
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static async Task DownloadAsync(string url, string dest, long max, IProgress<FetchProgress>? progress, CancellationToken callerCt)
    {
        // Inactivity timeout: restarted after every chunk, so slow-but-moving downloads are fine.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        stall.CancelAfter(StallTimeout);
        var ct = stall.Token;
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw new ApkFetchException(ApkFetchError.NotFound);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new ApkFetchException(ApkFetchError.Blocked);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentType?.MediaType is "text/html") throw new ApkFetchException(ApkFetchError.NotFound);

        var total = resp.Content.Headers.ContentLength;
        if (total > max) throw new ApkFetchException(ApkFetchError.TooLarge);
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buf = new byte[81920];
        long got = 0;
        long lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            stall.CancelAfter(StallTimeout);
            got += n;
            if (got > max) throw new ApkFetchException(ApkFetchError.TooLarge);
            if (got - lastReport >= 256 * 1024) { lastReport = got; progress?.Report(new FetchProgress(got, total)); }
        }
        progress?.Report(new FetchProgress(got, total));
    }

    private static bool HasZipMagic(string path)
    {
        using var fs = File.OpenRead(path);
        return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
    }

    private static bool HasEntry(string zipPath, string name)
    {
        using var z = ZipFile.OpenRead(zipPath);
        return z.GetEntry(name) is not null;
    }

    /// <summary>XAPK / split bundles carry several APKs; pick {package}.apk, else the largest one.</summary>
    private void ExtractBaseApk(string bundle, string dest, string packageId, CancellationToken ct)
    {
        using var z = ZipFile.OpenRead(bundle);
        if (z.Entries.Count > _maxEntries) throw new ApkFetchException(ApkFetchError.TooLarge);
        var apks = z.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var pick = apks.FirstOrDefault(e => string.Equals(e.Name, packageId + ".apk", StringComparison.OrdinalIgnoreCase))
                   ?? apks.OrderByDescending(e => e.Length).FirstOrDefault()
                   ?? throw new ApkFetchException(ApkFetchError.NotApk);
        if (pick.Length > _maxDownload) throw new ApkFetchException(ApkFetchError.TooLarge);
        using (var src = pick.Open())
        using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            CopyLimited(src, dst, _maxDownload, ct);
        if (!HasEntry(dest, "AndroidManifest.xml")) throw new ApkFetchException(ApkFetchError.NotApk);
    }

    /// <summary>
    /// Unpacks with hard limits on entry count and on bytes actually written (declared sizes in a hostile
    /// ZIP can lie), and refuses entries that would land outside <paramref name="destDir"/>.
    /// </summary>
    private void SafeExtract(string zipPath, string destDir, CancellationToken ct)
    {
        using var z = ZipFile.OpenRead(zipPath);
        if (z.Entries.Count > _maxEntries) throw new ApkFetchException(ApkFetchError.TooLarge);
        long declared = 0;
        foreach (var e in z.Entries)
        {
            declared += e.Length;
            if (declared > _maxUnpacked) throw new ApkFetchException(ApkFetchError.TooLarge);
        }

        var root = Path.GetFullPath(destDir);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        long budget = _maxUnpacked;
        foreach (var e in z.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new ApkFetchException(ApkFetchError.NotApk);
            if (e.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = e.Open();
            using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            budget -= CopyLimited(src, dst, budget, ct);
        }
    }

    private static long CopyLimited(Stream src, Stream dst, long budget, CancellationToken ct)
    {
        var buf = new byte[81920];
        long copied = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            copied += n;
            if (copied > budget) throw new ApkFetchException(ApkFetchError.TooLarge);
            dst.Write(buf, 0, n);
        }
        return copied;
    }
}
