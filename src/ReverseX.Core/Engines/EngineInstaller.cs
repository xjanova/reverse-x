using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReverseX.Core.Engines;

/// <summary>Why an install failed. The UI maps this to Thai/English text; cancellation is a plain <see cref="OperationCanceledException"/>.</summary>
public enum EngineInstallError
{
    /// <summary>Not enough free disk space (see <see cref="EngineInstallException.RequiredBytes"/> / <see cref="EngineInstallException.AvailableBytes"/>).</summary>
    NoSpace,
    /// <summary>Could not reach or finish reading the download (HTTP error, connection drop, 60 s without data).</summary>
    Network,
    /// <summary>The download's size or SHA-256 is not the pinned one.</summary>
    HashMismatch,
    /// <summary>The verified file is not a usable archive (unreadable ZIP, unsafe entry path, probe file missing).</summary>
    BadArchive,
    /// <summary>Local file-system error (access denied, locked file, path problems).</summary>
    Io,
    /// <summary>Another install into the same folder is already running (this or another app window).</summary>
    Busy,
}

public sealed class EngineInstallException : Exception
{
    public EngineInstallError Kind { get; }

    /// <summary>The engine being installed when it failed; null for checks made before any engine started.</summary>
    public EngineSpec? Engine { get; }

    /// <summary>Set for <see cref="EngineInstallError.NoSpace"/> when known.</summary>
    public long RequiredBytes { get; init; }

    public long AvailableBytes { get; init; }

    public EngineInstallException(EngineInstallError kind, EngineSpec? engine = null, Exception? inner = null)
        : base(engine is null ? kind.ToString() : $"{kind} ({engine.Id})", inner)
    {
        Kind = kind;
        Engine = engine;
    }
}

public enum EngineInstallPhase { Download, Verify, Extract, Installed }

/// <param name="Done">Bytes downloaded (Download/Verify) or extracted (Extract).</param>
/// <param name="Index">0-based position of <paramref name="Engine"/> in this install run.</param>
public readonly record struct EngineInstallProgress(EngineSpec Engine, EngineInstallPhase Phase, long Done, long Total, int Index, int Count);

/// <summary>Written next to an installed engine folder as <c>&lt;FolderName&gt;.installed.json</c>.</summary>
public sealed record EngineInstallMarker(string Id, string Version, string Sha256, string Url, DateTime InstalledUtc);

/// <summary>
/// Downloads pinned engines into <c>&lt;root&gt;\engines\</c>: streamed to a <c>.part</c> file while hashing, size + SHA-256
/// checked, unpacked into a private staging folder (zip-slip safe), probe-checked, then moved into place in one rename.
/// Any failure or cancellation removes the <c>.part</c> file and staging folder; an already valid install is never touched.
/// </summary>
/// <remarks>
/// Long paths: Ghidra has deep entries. .NET 8 handles paths past MAX_PATH itself, so extraction and the move work even
/// when the Windows LongPathsEnabled policy is off. The final folder (<c>%LOCALAPPDATA%\ReverseX\engines\ghidra_12.1.4_PUBLIC</c>)
/// is short, so Ghidra's own Java/batch launchers see ordinary-length paths; only a very long user name or a deeply
/// nested REVERSEX_DATA_DIR could push its deepest files past 260 characters.
/// </remarks>
public sealed class EngineInstaller
{
    private static readonly HttpClient SharedHttp = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // GitHub release assets redirect to objects.githubusercontent.com; integrity comes from the pinned SHA-256.
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ReverseX/{typeof(EngineInstaller).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return c;
    }

    public const string DownloadDirName = ".download";
    public const string StagingPrefix = ".staging-";
    private const string LockFileName = ".install.lock";
    private const int ReportEveryBytes = 512 * 1024;
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    private readonly string? _root;
    private readonly HttpClient _http;
    private readonly Func<string, long?> _freeBytes;

    /// <param name="root">Data root holding <c>engines\</c>; defaults to <see cref="AppPaths.Root"/>.</param>
    /// <param name="http">Client for downloads; defaults to a shared client without an overall timeout (a 60 s stall timeout applies).</param>
    /// <param name="freeBytes">Free bytes on the drive holding a path, or null if unknown; defaults to <see cref="DriveInfo"/>.</param>
    public EngineInstaller(string? root = null, HttpClient? http = null, Func<string, long?>? freeBytes = null)
    {
        _root = root;
        _http = http ?? SharedHttp;
        _freeBytes = freeBytes ?? DriveFreeBytes;
    }

    public string EnginesDir => _root is null ? AppPaths.EnginesDir : Path.Combine(_root, "engines");

    public string InstallDir(EngineSpec spec) => Path.Combine(EnginesDir, spec.FolderName);

    public string MarkerPath(EngineSpec spec) => Path.Combine(EnginesDir, spec.FolderName + ".installed.json");

    public bool IsInstalled(EngineSpec spec) => EngineLocator.IsValidInstall(spec, InstallDir(spec));

    /// <summary>Peak disk need for one engine: the download plus room to unpack it (~3x for a ZIP).</summary>
    public static long RequiredBytesFor(EngineSpec spec) =>
        spec.Packaging == EnginePackaging.ZipArchive ? spec.Size * 4 : spec.Size;

    /// <summary>Free space needed to install every catalog engine that is not installed yet.</summary>
    public long RequiredFreeBytes => RequiredFreeBytesFor(EngineCatalog.All);

    public long RequiredFreeBytesFor(IEnumerable<EngineSpec> which) =>
        Unique(which).Where(s => !IsInstalled(s)).Sum(RequiredBytesFor);

    /// <summary>Free bytes on the drive holding the engines folder, or null if it cannot be measured.</summary>
    public long? AvailableFreeBytes => _freeBytes(_root ?? AppPaths.Root);

    /// <summary>
    /// Installs each engine in <paramref name="which"/> that is not already valid, in order. Engines finished before a
    /// failure stay installed. Throws <see cref="EngineInstallException"/> or <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task InstallAsync(IEnumerable<EngineSpec> which, IProgress<EngineInstallProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(which);
        var list = Unique(which).ToList();
        foreach (var spec in list) ValidateSpec(spec);
        // File work (stale cleanup, unzip, deletes) must not run on the caller's UI thread.
        return Task.Run(() => InstallAllAsync(list, progress, ct), CancellationToken.None);
    }

    private async Task InstallAllAsync(List<EngineSpec> list, IProgress<EngineInstallProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engines = EnginesDir;
        try { Directory.CreateDirectory(engines); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Wrap(ex, null); }

        using var installLock = AcquireLock(engines);
        CleanStale(engines);

        var need = list.Where(s => !IsInstalled(s)).Sum(RequiredBytesFor);
        if (need > 0 && _freeBytes(engines) is { } free && free < need)
            throw new EngineInstallException(EngineInstallError.NoSpace) { RequiredBytes = need, AvailableBytes = free };

        for (var i = 0; i < list.Count; i++)
        {
            var spec = list[i];
            if (!IsInstalled(spec)) await InstallOneAsync(spec, i, list.Count, progress, ct).ConfigureAwait(false);
            progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Installed, spec.Size, spec.Size, i, list.Count));
        }
    }

    private async Task InstallOneAsync(EngineSpec spec, int index, int count, IProgress<EngineInstallProgress>? progress, CancellationToken ct)
    {
        var engines = EnginesDir;
        var downloadDir = Path.Combine(engines, DownloadDirName);
        var part = Path.Combine(downloadDir, spec.DownloadName + ".part");
        var staging = Path.Combine(engines, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(downloadDir);
            await DownloadAsync(spec, part, index, count, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var stagedTop = Path.Combine(staging, spec.FolderName);
            if (spec.Packaging == EnginePackaging.ZipArchive)
            {
                Extract(spec, part, staging, index, count, progress, ct);
                File.Delete(part);
            }
            else
            {
                var dest = Path.Combine(stagedTop, spec.ProbeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? stagedTop);
                File.Move(part, dest);
            }

            if (!EngineLocator.IsValidInstall(spec, stagedTop)) throw new EngineInstallException(EngineInstallError.BadArchive, spec);
            ct.ThrowIfCancellationRequested();
            await CommitAsync(spec, stagedTop, ct).ConfigureAwait(false);
        }
        catch (EngineInstallException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw new EngineInstallException(EngineInstallError.Network, spec, ex); }
        catch (HttpRequestException ex) { throw new EngineInstallException(EngineInstallError.Network, spec, ex); }
        catch (InvalidDataException ex) { throw new EngineInstallException(EngineInstallError.BadArchive, spec, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Wrap(ex, spec); }
        finally
        {
            TryDeleteFile(part);
            TryDeleteDir(staging);
            TryDeleteIfEmpty(downloadDir);
        }
    }

    private async Task DownloadAsync(EngineSpec spec, string part, int index, int count, IProgress<EngineInstallProgress>? progress, CancellationToken callerCt)
    {
        // Inactivity timeout, restarted after every chunk: slow but moving downloads are fine.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        stall.CancelAfter(StallTimeout);
        var ct = stall.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, spec.Url);
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new EngineInstallException(EngineInstallError.Network, spec,
                new HttpRequestException($"HTTP {(int)resp.StatusCode} for {spec.Url}", null, resp.StatusCode));
        if (resp.Content.Headers.ContentLength is { } declared && declared != spec.Size)
            throw new EngineInstallException(EngineInstallError.HashMismatch, spec);

        progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Download, 0, spec.Size, index, count));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long got = 0, lastReport = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n;
                try { n = await src.ReadAsync(buf, ct).ConfigureAwait(false); }
                catch (IOException ex) { throw new EngineInstallException(EngineInstallError.Network, spec, ex); }
                if (n == 0) break;
                got += n;
                if (got > spec.Size) throw new EngineInstallException(EngineInstallError.HashMismatch, spec);
                sha.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                stall.CancelAfter(StallTimeout);
                if (got - lastReport >= ReportEveryBytes)
                {
                    lastReport = got;
                    progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Download, got, spec.Size, index, count));
                }
            }
        }
        progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Download, got, spec.Size, index, count));

        progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Verify, got, spec.Size, index, count));
        var hex = Convert.ToHexString(sha.GetHashAndReset());
        if (got != spec.Size || !hex.Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new EngineInstallException(EngineInstallError.HashMismatch, spec);
    }

    /// <summary>Checks every entry path before writing anything, then unpacks; any unsafe entry fails the whole archive.</summary>
    private static void Extract(EngineSpec spec, string zipPath, string staging, int index, int count,
        IProgress<EngineInstallProgress>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(staging);
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDir)>(zip.Entries.Count);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            var target = SafeTarget(rootPrefix, e.FullName) ?? throw new EngineInstallException(EngineInstallError.BadArchive, spec);
            var isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            plan.Add((e, target, isDir));
            if (!isDir) total += e.Length;
        }

        Directory.CreateDirectory(root);
        progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Extract, 0, total, index, count));
        long done = 0, lastReport = 0;
        var buf = new byte[1 << 16];
        foreach (var (entry, target, isDir) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (isDir) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? root);
            using var src = entry.Open();
            using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buf, 0, n);
                done += n;
                if (done - lastReport >= ReportEveryBytes)
                {
                    lastReport = done;
                    progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Extract, done, total, index, count));
                }
            }
        }
        progress?.Report(new EngineInstallProgress(spec, EngineInstallPhase.Extract, done, total, index, count));
    }

    /// <summary>Full target path for a ZIP entry, or null if it is absolute, has a drive/stream colon or escapes the root.</summary>
    private static string? SafeTarget(string rootPrefix, string entryName)
    {
        if (string.IsNullOrEmpty(entryName)) return null;
        if (entryName[0] is '/' or '\\' || entryName.Contains(':') || entryName.Contains('\0') || Path.IsPathRooted(entryName)) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootPrefix, entryName)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Moves the verified folder into place (replacing only a broken one) and writes the marker.</summary>
    private async Task CommitAsync(EngineSpec spec, string stagedTop, CancellationToken ct)
    {
        if (IsInstalled(spec)) return; // cannot normally happen under the lock; never touch a valid install
        var target = InstallDir(spec);
        var marker = MarkerPath(spec);
        TryDeleteFile(marker);
        if (File.Exists(target)) File.Delete(target);
        // Directory.Delete does not follow junctions/symlinks, so a linked folder only loses the link.
        else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

        // Antivirus scanners briefly hold freshly written files, which makes the rename fail; retry a few times.
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(stagedTop, target); break; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5 && !Directory.Exists(target))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }

        try
        {
            var info = new EngineInstallMarker(spec.Id, spec.Version, spec.Sha256.ToLowerInvariant(), spec.Url, DateTime.UtcNow);
            File.WriteAllText(marker, JsonSerializer.Serialize(info, MarkerJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The install itself is valid (probe file present); the marker is informational only.
            AppPaths.Log($"Could not write engine marker {marker}", ex);
        }
    }

    private static FileStream AcquireLock(string engines)
    {
        try
        {
            return new FileStream(Path.Combine(engines, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when (ex.HResult is ErrorSharingViolation or ErrorLockViolation)
        {
            throw new EngineInstallException(EngineInstallError.Busy, null, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Wrap(ex, null);
        }
    }

    /// <summary>Leftovers of a run that was killed (app closed, power loss). Safe because the install lock is held.</summary>
    private static void CleanStale(string engines)
    {
        TryDeleteDir(Path.Combine(engines, DownloadDirName));
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(engines, StagingPrefix + "*")) TryDeleteDir(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not list stale engine staging folders", ex);
        }
    }

    private static EngineInstallException Wrap(Exception ex, EngineSpec? spec) =>
        new(ex is IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } ? EngineInstallError.NoSpace : EngineInstallError.Io, spec, ex);

    private static IEnumerable<EngineSpec> Unique(IEnumerable<EngineSpec> which) =>
        which.DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase);

    private static void ValidateSpec(EngineSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var folderOk = spec.FolderName.Length > 0 && spec.FolderName is not ("." or "..")
                       && spec.FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        var probeOk = spec.ProbeFile.Length > 0 && !Path.IsPathRooted(spec.ProbeFile)
                      && !spec.ProbeFile.Split('/', '\\').Contains("..");
        if (!folderOk || !probeOk || spec.Size <= 0 || spec.Sha256.Length != 64)
            throw new ArgumentException($"Engine spec '{spec.Id}' is not installable.", nameof(spec));
    }

    private static long? DriveFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null; // UNC shares and odd mounts: do not block the install on an unknown answer
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log($"Could not delete {path}", ex); }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log($"Could not delete {path}", ex); }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log($"Could not delete {path}", ex); }
    }
}
