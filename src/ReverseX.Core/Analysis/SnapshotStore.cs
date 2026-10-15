using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReverseX.Core.Analysis;

/// <summary>Saves finished analyses under %LOCALAPPDATA%\ReverseX\projects\&lt;id&gt;\snapshot.json.gz so reopening needs no new analysis.</summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public SnapshotStore(string? root = null) => _root = root ?? Path.Combine(AppPaths.Root, "projects");

    /// <summary>Stable id for a path: first 16 hex chars of SHA-256 over the lower-cased full path.</summary>
    public static string IdFor(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
    }

    public string DirFor(string sourcePath) => Path.Combine(_root, IdFor(sourcePath));
    /// <param name="slot">Lets two pages keep separate results for the same file (e.g. REA view vs local metadata view).</param>
    public string FileFor(string sourcePath, string? slot = null) =>
        Path.Combine(DirFor(sourcePath), slot is null ? "snapshot.json.gz" : $"snapshot.{slot}.json.gz");

    public static (long Size, DateTime WriteUtc) Stamp(string path)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return (fi.Length, fi.LastWriteTimeUtc);
        }
        if (Directory.Exists(path)) return (0, Directory.GetLastWriteTimeUtc(path));
        return (0, DateTime.MinValue);
    }

    public void Save(AnalysisSnapshot snapshot, string? slot = null)
    {
        var file = FileFor(snapshot.SourcePath, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, snapshot, Json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Returns null when there is no usable snapshot (missing, unreadable or from an older format).</summary>
    public AnalysisSnapshot? TryLoad(string sourcePath, string? slot = null)
    {
        var file = FileFor(sourcePath, slot);
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var snap = JsonSerializer.Deserialize<AnalysisSnapshot>(gz, Json);
            return snap is { Version: AnalysisSnapshot.CurrentVersion } ? snap : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not read snapshot " + file, ex);
            return null;
        }
    }

    public void Delete(string sourcePath)
    {
        try { Directory.Delete(DirFor(sourcePath), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True when the source file changed after the snapshot was taken.</summary>
    public static bool IsStale(AnalysisSnapshot snap)
    {
        var (size, write) = Stamp(snap.SourcePath);
        if (write == DateTime.MinValue) return false; // source gone: keep showing the snapshot
        return size != snap.SourceSize || Math.Abs((write - snap.SourceWriteUtc).TotalSeconds) > 2;
    }
}
