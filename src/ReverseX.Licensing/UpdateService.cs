using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ReverseX.Licensing;

/// <summary>
/// Self-update from xman4289.com (Free and Pro alike), like Anti X: GET /update/check on start and every
/// 6 hours; a newer release is downloaded from xman4289.com only (redirects refused), checked against the
/// size and SHA-256 the server announced and against the ECDSA-signed manifest inside the package, then
/// installed by the new version's own <see cref="Updater"/> after this app exits (it can roll back).
/// Reverse X is a per-user program, so unlike Anti X nothing runs as SYSTEM and the staging folder is the
/// user's own data folder.
/// </summary>
public sealed class UpdateService : IUpdateSource, IDisposable
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    internal const long MaxPackageBytes = 1024L * 1024 * 1024;

    private readonly IXmanClient _api;
    private readonly string _stagingRoot;
    private readonly string _dataDir;
    private readonly string _installDir;
    private readonly string _version;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private UpdateStatus _status;
    private Announced? _latest;

    private sealed record Announced(string Version, string DownloadUrl, string? Sha256, long Size);

    public event Action? Changed;

    // ช่องสำหรับเทสต์: ตัวดาวน์โหลด, กุญแจตรวจลายเซ็น, การเปิดตัวติดตั้ง
    internal Func<HttpClient> DownloadClient { get; init; }
    internal byte[]? ReleaseKeyOverride { get; init; }
    internal Action<ProcessStartInfo> Launch { get; init; } = psi => Process.Start(psi)?.Dispose();
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public UpdateService(string dataDir, string installDir, string version, Action<string> log)
        : this(new XmanApi("ReverseX-Update/" + version), dataDir, installDir, version, log)
    {
    }

    internal UpdateService(IXmanClient api, string dataDir, string installDir, string version, Action<string> log)
    {
        _api = api;
        _dataDir = dataDir;
        _stagingRoot = Path.Combine(dataDir, "update");
        _installDir = installDir;
        _version = version;
        _log = log;
        _status = new UpdateStatus { Current = version };
        DownloadClient = () => XmanApi.CreateClient("ReverseX-Update/" + version, TimeSpan.FromMinutes(30), followRedirects: false);
        LastOutcome = Updater.TakeOutcome(dataDir);
    }

    public Updater.Outcome? LastOutcome { get; }

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    private void Set(UpdateState state, UpdateError error = UpdateError.None, bool? available = null, string? latest = null,
        double progress = 0, string? detail = null, bool stampChecked = false)
    {
        lock (_gate)
        {
            _status = new UpdateStatus
            {
                Current = _version,
                Latest = latest ?? _status.Latest,
                Available = available ?? _status.Available,
                State = state,
                Error = error,
                Progress = progress,
                Detail = detail,
                CheckedAt = stampChecked ? DateTime.UtcNow : _status.CheckedAt,
            };
        }
        Changed?.Invoke();
    }

    /// <summary>First check after 20 seconds (the app is idle by then), then every 6 hours. Installing waits for the user.</summary>
    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        CleanStaging();
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log("Update check failed: " + ex.Message); }
            try { await Task.Delay(CheckEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<UpdateResult> CheckAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            Set(UpdateState.Checking);
            var reply = await _api.GetAsync("/update/check?current_version=" + Uri.EscapeDataString(_version.Split('+')[0])).ConfigureAwait(false);
            if (!reply.IsDefinitive || reply.Json is null)
            {
                Set(UpdateState.Idle, UpdateError.Offline, stampChecked: true);
                return new UpdateResult(false, UpdateError.Offline);
            }
            var j = reply.Json;
            var latest = (j.Str("latest_version") ?? "").Trim().TrimStart('v', 'V');
            var url = j.Str("download_url") ?? "";
            var newer = j.Bool("has_update") == true && UpdatePackageVerifier.IsNewer(latest, _version);
            if (!newer)
            {
                _latest = null;
                Set(UpdateState.Idle, available: false, latest: latest.Length > 0 ? latest : _version, stampChecked: true);
                return new UpdateResult(true);
            }
            // ดาวน์โหลดจาก xman4289.com เท่านั้น
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !XmanApi.IsXmanUrl(uri))
            {
                _latest = null;
                Set(UpdateState.Failed, UpdateError.NotXmanUrl, available: false, latest: latest, stampChecked: true);
                return new UpdateResult(false, UpdateError.NotXmanUrl);
            }
            _latest = new Announced(latest, url, j.Str("sha256"), j.Long("file_size") ?? 0);
            Set(UpdateState.Idle, available: true, latest: latest, stampChecked: true);
            return new UpdateResult(true);
        }
        finally
        {
            _busy.Release();
        }
    }

    public async Task<UpdateResult> InstallAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        string? staging = null;
        try
        {
            var a = _latest;
            if (a is null) return Fail(UpdateError.NothingToInstall);
            if (string.IsNullOrWhiteSpace(a.Sha256) || a.Sha256.Trim().Length != 64) return Fail(UpdateError.NoChecksum);
            if (!CanWrite(_installDir)) return Fail(UpdateError.InstallDirReadOnly, _installDir);

            staging = Path.Combine(_stagingRoot, a.Version + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, $"ReverseX-{a.Version}.zip");

            Set(UpdateState.Downloading);
            if (await DownloadAsync(a, zip).ConfigureAwait(false) is { } refused) return Fail(refused.Error, refused.Detail, staging);

            Set(UpdateState.Verifying);
            var unpacked = Path.Combine(staging, "files");
            try { ExtractSafely(zip, unpacked); }
            catch (InvalidDataException ex) { return Fail(UpdateError.BadArchive, ex.Message, staging); }
            var appDir = Directory.EnumerateFiles(unpacked, UpdatePackageVerifier.ManifestFileName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, UpdatePackageVerifier.MainExe)));
            if (appDir is null) return Fail(UpdateError.NoAppFolder, null, staging);
            var verdict = ReleaseKeyOverride is { } testKey
                ? UpdatePackageVerifier.Verify(appDir, a.Version, _version, testKey)
                : UpdatePackageVerifier.Verify(appDir, a.Version, _version);
            if (verdict != UpdatePackageVerifier.Result.Valid)
            {
                var error = verdict switch
                {
                    UpdatePackageVerifier.Result.Unsigned => UpdateError.Unsigned,
                    UpdatePackageVerifier.Result.WrongVersion => UpdateError.WrongVersion,
                    UpdatePackageVerifier.Result.FilesMismatch => UpdateError.FilesMismatch,
                    _ => UpdateError.BadSignature,
                };
                return Fail(error, verdict.ToString(), staging);
            }
            try { File.Delete(zip); } catch (IOException) { /* ลบรอบหน้า */ }

            Set(UpdateState.Installing, latest: a.Version);
            var psi = new ProcessStartInfo(Path.Combine(appDir, UpdatePackageVerifier.MainExe))
            {
                UseShellExecute = false,
                WorkingDirectory = appDir,
            };
            psi.ArgumentList.Add(Updater.Command);
            psi.ArgumentList.Add(appDir);
            psi.ArgumentList.Add(_installDir);
            psi.ArgumentList.Add(a.Version);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(_dataDir);
            Launch(psi);
            _latest = null;
            _log($"Updater started for {a.Version}");
            return new UpdateResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Fail(UpdateError.IoError, ex.Message, staging);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Unpacks with the same limits as the APK fetcher: no path outside the folder, no more than the package cap.</summary>
    private static void ExtractSafely(string zip, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        long total = 0;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 50_000) throw new InvalidDataException("too many entries");
        foreach (var entry in archive.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("entry outside the package: " + entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = entry.Open();
            using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes * 3) throw new InvalidDataException("package expands too much");
                output.Write(buffer, 0, read);
            }
        }
    }

    internal static bool CanWrite(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".rx-write-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes downloads of earlier rounds (the updater that used them finished long ago).</summary>
    private void CleanStaging()
    {
        try
        {
            if (!Directory.Exists(_stagingRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_stagingRoot))
                if (!Path.GetFileName(dir).Equals("backup", StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("Update staging cleanup: " + ex.Message);
        }
    }

    private UpdateResult Fail(UpdateError error, string? detail = null, string? staging = null)
    {
        // ไฟล์ที่ไม่ผ่านไม่เก็บไว้ในเครื่อง
        if (staging is not null)
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("Update staging not removed: " + ex.Message); }
        }
        Set(UpdateState.Failed, error, detail: detail);
        _log($"Update refused: {error} {detail}");
        return new UpdateResult(false, error, detail);
    }

    private sealed record Refusal(UpdateError Error, string? Detail = null);

    /// <summary>Null on success, else why the file was refused.</summary>
    private async Task<Refusal?> DownloadAsync(Announced a, string path)
    {
        try
        {
            using var http = DownloadClient();
            using var response = await http.GetAsync(a.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399) return new(UpdateError.Redirected, response.Headers.Location?.ToString());
            if (!response.IsSuccessStatusCode) return new(UpdateError.HttpError, "HTTP " + (int)response.StatusCode);
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new(UpdateError.NotAPackage, media);
            var length = response.Content.Headers.ContentLength;
            if (a.Size > 0 && length is { } l && l != a.Size) return new(UpdateError.SizeMismatch, $"{l} != {a.Size}");
            var limit = a.Size > 0 ? a.Size : MaxPackageBytes;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var buffer = new byte[81920];
            var lastShown = 0.0;
            await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                using var idle = new CancellationTokenSource();
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);
                    var read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > limit) return new(UpdateError.TooLarge);
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    if (a.Size > 0 && (double)total / a.Size - lastShown >= 0.02)
                    {
                        lastShown = (double)total / a.Size;
                        Set(UpdateState.Downloading, progress: lastShown);
                    }
                }
            }
            if (a.Size > 0 && total != a.Size) return new(UpdateError.Incomplete, $"{total} / {a.Size}");
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(a.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(UpdateError.HashMismatch);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException or OperationCanceledException or IOException)
        {
            return new(UpdateError.Incomplete, ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
