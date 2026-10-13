using System.Text;

namespace ReverseX.Core.Analysis;

/// <summary>
/// Reads the build info a Go linker embeds (Go 1.18+: "\xff Go buildinf:" header with inline version + module info):
/// compiler version, main module, dependencies and build settings. Read-only, no REA needed.
/// REA 6.4.0 has no inspect_go_binary tool (checked against its 170-tool list), so this fills the gap locally.
/// </summary>
public static class GoBuildInfo
{
    private static readonly byte[] Magic = Encoding.Latin1.GetBytes("\u00ff Go buildinf:");
    private const long MaxScanBytes = 768L * 1024 * 1024;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int BlobSize = 2 * 1024 * 1024;

    public static GoBuildInfoModel? TryRead(string path, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var buf = new byte[ChunkSize + Magic.Length];
            long pos = 0;
            var carry = 0;
            var limit = Math.Min(fs.Length, MaxScanBytes);
            while (pos < limit)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = pos;
                var read = fs.Read(buf, carry, ChunkSize);
                if (read <= 0) break;
                var span = buf.AsSpan(0, carry + read);
                var from = 0;
                while (from < span.Length)
                {
                    var idx = span[from..].IndexOf(Magic);
                    if (idx < 0) break;
                    var at = from + idx;
                    var model = ParseAtOffset(fs, pos - carry + at);
                    if (model is not null) return model;
                    from = at + 1;
                }
                carry = Math.Min(Magic.Length - 1, span.Length);
                span[^carry..].CopyTo(buf);
                pos += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Go build info read failed: " + path, ex);
        }
        return null;
    }

    private static GoBuildInfoModel? ParseAtOffset(FileStream fs, long offset)
    {
        fs.Position = offset;
        var blob = new byte[(int)Math.Min(BlobSize, fs.Length - offset)];
        var n = fs.Read(blob, 0, blob.Length);
        return Parse(blob.AsSpan(0, n));
    }

    /// <summary>Parses a buffer that starts at the build-info magic. Null if it is not an inline-string (Go 1.18+) header.</summary>
    public static GoBuildInfoModel? Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 34 || !blob[..Magic.Length].SequenceEqual(Magic)) return null;
        var ptrSize = blob[14];
        var flags = blob[15];
        if ((ptrSize != 4 && ptrSize != 8) || (flags & 2) == 0) return null;

        var p = 32;
        if (!ReadString(blob, ref p, out var version) || !version.StartsWith("go", StringComparison.Ordinal)) return null;
        ReadString(blob, ref p, out var mod);

        // modinfo is wrapped by two 16-byte sentinels
        if (mod.Length >= 33) mod = mod[16..^16];
        var deps = new List<GoModule>();
        var settings = new List<GoSetting>();
        string? path = null, main = null, mainVer = null;
        foreach (var raw in mod.Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "path" when f.Length > 1: path = f[1]; break;
                case "mod" when f.Length > 1: main = f[1]; mainVer = f.Length > 2 ? f[2] : null; break;
                case "dep" when f.Length > 1: deps.Add(new GoModule(f[1], f.Length > 2 ? f[2] : "")); break;
                case "=>" when f.Length > 2 && deps.Count > 0:
                    deps[^1] = new GoModule(deps[^1].Path + " => " + f[1], f[2]);
                    break;
                case "build" when f.Length > 1:
                    var kv = f[1];
                    var eq = kv.IndexOf('=');
                    settings.Add(eq > 0 ? new GoSetting(kv[..eq].TrimStart('-'), kv[(eq + 1)..]) : new GoSetting(kv.TrimStart('-'), ""));
                    break;
            }
        }
        return new GoBuildInfoModel { Version = version, Path = path, MainModule = main, MainVersion = mainVer, Dependencies = deps, Settings = settings };
    }

    private static bool ReadString(ReadOnlySpan<byte> b, ref int p, out string s)
    {
        s = "";
        ulong len = 0;
        var shift = 0;
        while (true)
        {
            if (p >= b.Length || shift > 56) return false;
            var x = b[p++];
            len |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
        }
        if (len > (ulong)(b.Length - p)) return false;
        s = Encoding.UTF8.GetString(b.Slice(p, (int)len));
        p += (int)len;
        return true;
    }
}
