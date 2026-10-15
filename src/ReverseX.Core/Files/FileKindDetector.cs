using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ReverseX.Core.Files;

/// <summary>Quick first-look classification by magic bytes / structure. It never executes or fully parses the target.</summary>
public static class FileKindDetector
{
    public static FileKindResult Detect(string path)
    {
        if (Directory.Exists(path)) return DetectFolder(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Path not found", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] head = new byte[4096];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            n = fs.Read(head, 0, head.Length);
        }
        var h = head.AsSpan(0, n);

        if (h.StartsWith("SQLite format 3\0"u8)) return new FileKindResult(FileKind.Sqlite);
        if (h.StartsWith(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }))
            return new FileKindResult(FileKind.Elf, ElfDetail(h));
        if (h.StartsWith(new byte[] { 0x00, (byte)'a', (byte)'s', (byte)'m' })) return new FileKindResult(FileKind.Wasm);
        if (IsMachO(h)) return new FileKindResult(FileKind.MachO);
        if (h.StartsWith(new byte[] { (byte)'M', (byte)'Z' })) return DetectPe(path);
        if (h.StartsWith(new byte[] { (byte)'P', (byte)'K' })) return DetectZip(path, ext);
        if (ext == ".asar" && IsAsarHeader(h)) return new FileKindResult(FileKind.Asar);
        if (ext == ".har" || LooksLikeHar(h)) return new FileKindResult(FileKind.Har);
        return new FileKindResult(FileKind.Unknown);
    }

    private static FileKindResult DetectFolder(string dir)
    {
        // Electron / packaged JS app: resources\app.asar, resources\app\, or a package.json that depends on electron.
        var res = Path.Combine(dir, "resources");
        if (File.Exists(Path.Combine(res, "app.asar")) || Directory.Exists(Path.Combine(res, "app")))
            return new FileKindResult(FileKind.AppFolder, "Electron");
        var pkg = Path.Combine(dir, "package.json");
        if (File.Exists(pkg))
        {
            try
            {
                var text = File.ReadAllText(pkg);
                if (text.Contains("\"electron\"", StringComparison.OrdinalIgnoreCase))
                    return new FileKindResult(FileKind.AppFolder, "Electron");
                return new FileKindResult(FileKind.AppFolder, "Node.js");
            }
            catch (IOException) { return new FileKindResult(FileKind.AppFolder, "Node.js"); }
        }
        // An installed program folder: an .exe next to .dll files.
        try
        {
            var hasExe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any();
            var hasDll = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Any();
            if (hasExe && hasDll) return new FileKindResult(FileKind.AppFolder, "Windows");
        }
        catch (UnauthorizedAccessException) { /* fall through to plain folder */ }
        return new FileKindResult(FileKind.Folder);
    }

    private static FileKindResult DetectPe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(fs);
            var headers = pe.PEHeaders;
            var arch = headers.CoffHeader.Machine switch
            {
                Machine.I386 => "x86",
                Machine.Amd64 => "x64",
                Machine.Arm64 => "ARM64",
                Machine.Arm => "ARM",
                var m => m.ToString(),
            };
            var magic = headers.PEHeader?.Magic == PEMagic.PE32Plus ? "PE32+" : "PE32";
            var type = headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ? "DLL" : "EXE";
            var isManaged = headers.CorHeader is not null && pe.HasMetadata;
            return new FileKindResult(isManaged ? FileKind.DotNet : FileKind.Pe, $"{magic} {arch} · {type}");
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return new FileKindResult(FileKind.Pe); // "MZ" but a damaged/odd header: still a PE-looking file
        }
    }

    private static FileKindResult DetectZip(string path, string ext)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("AndroidManifest.xml") is not null) return new FileKindResult(FileKind.Apk);
            // Split-APK bundles (XAPK/APKS/APKM): a zip holding base.apk and/or several .apk splits.
            var apks = zip.Entries.Count(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
            if (apks > 0 && (ext is ".xapk" or ".apks" or ".apkm" || zip.GetEntry("manifest.json") is not null
                             || zip.Entries.Any(e => e.Name.Equals("base.apk", StringComparison.OrdinalIgnoreCase))))
                return new FileKindResult(FileKind.Apk, ext is ".xapk" or ".apks" or ".apkm" ? ext[1..].ToUpperInvariant() : "XAPK");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { /* not a readable zip */ }
        return ext == ".apk" ? new FileKindResult(FileKind.Apk) : new FileKindResult(FileKind.Unknown, "ZIP");
    }

    // Electron .asar starts with a Pickle: uint32 payload size (= 4), then the header Pickle.
    private static bool IsAsarHeader(ReadOnlySpan<byte> h) =>
        h.Length >= 16 && h[0] == 4 && h[1] == 0 && h[2] == 0 && h[3] == 0;

    private static bool IsMachO(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4) return false;
        var be = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
        return be is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE && !IsJavaClass(h);
    }

    // 0xCAFEBABE is shared with Java .class files (version field is 45+); fat Mach-O has a small arch count.
    private static bool IsJavaClass(ReadOnlySpan<byte> h) =>
        h.Length >= 8 && h[0] == 0xCA && h[1] == 0xFE && h[2] == 0xBA && h[3] == 0xBE && (h[6] << 8 | h[7]) >= 45;

    private static bool LooksLikeHar(ReadOnlySpan<byte> h)
    {
        var text = Encoding.UTF8.GetString(h).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return text.StartsWith('{') && text.Contains("\"log\"", StringComparison.Ordinal)
            && (text.Contains("\"entries\"", StringComparison.Ordinal) || text.Contains("\"creator\"", StringComparison.Ordinal));
    }

    private static string? ElfDetail(ReadOnlySpan<byte> h)
    {
        if (h.Length < 20) return null;
        var bits = h[4] == 2 ? "64-bit" : "32-bit";
        var machine = h[5] == 1 ? h[18] | h[19] << 8 : h[18] << 8 | h[19];
        var arch = machine switch { 0x3E => "x86-64", 0x03 => "x86", 0xB7 => "ARM64", 0x28 => "ARM", 0xF3 => "RISC-V", _ => "?" };
        return $"{bits} {arch}";
    }
}
