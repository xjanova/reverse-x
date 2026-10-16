using System.Security.Cryptography;
using System.Text.Json;

namespace ReverseX.Licensing;

/// <summary>
/// Proves an unpacked update came from the Reverse X release pipeline (same scheme as Anti X / WinXTools).
///
/// HTTPS and the server's SHA-256 only prove the file arrived as sent. So every release carries
/// update-manifest.json (product, version, size + SHA-256 of every file) and update-manifest.sig, an ECDSA
/// P-256 signature made in CI with a key that never leaves GitHub secrets. Nothing is installed unless the
/// signature verifies against the public key below, the version is the announced one and newer, and the
/// folder holds exactly the listed files, byte for byte.
/// </summary>
public static class UpdatePackageVerifier
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.sig";
    public const string ProductSlug = "reverse-x";
    public const string MainExe = "ReverseX.exe";

    // SubjectPublicKeyInfo of the release signing key (ECDSA P-256), created 2026-10-16 with tools/ReleaseSigner keygen.
    // The private half is the REVERSEX_UPDATE_SIGNING_KEY secret of github.com/xjanova/reverse-x
    // (owner backup: %USERPROFILE%\.xman-signing\reversex-update-signing-key.txt). It is never in this repository.
    public const string ReleasePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyXmJVzuRxgHQ5ACkMVLiKJFdzGBSxwr33VuRA67SYYVQUAgvTo1GOi4uCjfbQ8kBmY7gq/E4bPmGrMxoVfGFpg==";

    private const long MaxManifestBytes = 4 * 1024 * 1024;

    public enum Result
    {
        Valid,
        /// <summary>No manifest/signature (or this build has no release key compiled in).</summary>
        Unsigned,
        BadSignature,
        /// <summary>Signed for another product or version than announced, or not newer.</summary>
        WrongVersion,
        /// <summary>A listed file is missing or differs, or an unlisted file is present.</summary>
        FilesMismatch,
    }

    public static Result Verify(string packageDir, string expectedVersion, string currentVersion) =>
        Verify(packageDir, expectedVersion, currentVersion, Convert.FromBase64String(ReleasePublicKey));

    /// <summary>Same, against another public key (tests, the signer's self-check).</summary>
    public static Result Verify(string packageDir, string expectedVersion, string currentVersion, byte[] publicKeySpki)
    {
        var manifestPath = Path.Combine(packageDir, ManifestFileName);
        var signaturePath = Path.Combine(packageDir, SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath)) return Result.Unsigned;
        if (new FileInfo(manifestPath).Length > MaxManifestBytes || new FileInfo(signaturePath).Length > 1024) return Result.BadSignature;

        var manifestBytes = File.ReadAllBytes(manifestPath);
        byte[] signature;
        try { signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim()); }
        catch (FormatException) { return Result.BadSignature; }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (!key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Result.BadSignature;
        }
        catch (CryptographicException)
        {
            return Result.BadSignature;
        }

        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return Result.FilesMismatch; }
        if (manifest?.Files is not { Count: > 0 }) return Result.FilesMismatch;

        if (!string.Equals(manifest.Product, ProductSlug, StringComparison.OrdinalIgnoreCase)
            || !SameVersion(manifest.Version, expectedVersion)
            || !IsNewer(manifest.Version, currentVersion))
            return Result.WrongVersion;

        var root = Path.GetFullPath(packageDir);
        var rootWithSlash = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Sha256 is not { Length: 64 }) return Result.FilesMismatch;
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':')) return Result.FilesMismatch;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) || !listed.Add(full)) return Result.FilesMismatch;
            var file = new FileInfo(full);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != entry.Size) return Result.FilesMismatch;
            using var stream = file.OpenRead();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return Result.FilesMismatch;
        }

        // ไฟล์ที่ไม่ได้อยู่ในรายการที่เซ็น (เช่น DLL ที่ถูกวางเพิ่ม) = ไม่ติดตั้ง
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Result.FilesMismatch;
            if (info.Attributes.HasFlag(FileAttributes.Directory)) continue;
            var inRoot = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(path);
            var isSignature = inRoot && (name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) || name.Equals(SignatureFileName, StringComparison.OrdinalIgnoreCase));
            if (!isSignature && !listed.Contains(Path.GetFullPath(path))) return Result.FilesMismatch;
        }
        return Result.Valid;
    }

    private static bool SameVersion(string? a, string? b) => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string? version) => (version ?? "").Trim().TrimStart('v', 'V');

    /// <summary>"1.3.0" &gt; "1.2.9"; a final release is newer than its own prerelease.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParseVersion(candidate, out var next, out var nextIsPre)) return false;
        if (!TryParseVersion(current, out var now, out var nowIsPre)) return true;
        return next > now || (next == now && nowIsPre && !nextIsPre);
    }

    /// <summary>"v1.2.0", "1.2.0-beta", "1.2" → 1.2.0 (+ prerelease flag). Build metadata ("+sha") is ignored.</summary>
    public static bool TryParseVersion(string? text, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0);
        isPrerelease = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = Trim(text);
        var cut = value.IndexOf('+');
        if (cut >= 0) value = value[..cut];
        cut = value.IndexOf('-');
        if (cut >= 0)
        {
            isPrerelease = true;
            value = value[..cut];
        }
        if (!value.Contains('.')) value += ".0";
        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private sealed class Manifest
    {
        public string? Product { get; set; }
        public string? Version { get; set; }
        public List<ManifestFile>? Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string? Path { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
