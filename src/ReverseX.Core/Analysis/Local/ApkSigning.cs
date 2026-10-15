using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Reads who signed an APK: JAR (v1) PKCS#7 blocks and the APK Signing Block (v2 / v3 / v3.1). Nothing is verified — it is identification only.</summary>
public static class ApkSigning
{
    private const uint V2Id = 0x7109871a, V3Id = 0xf05368c0, V31Id = 0x1b93ad61, SourceStampId = 0x6dff800d;

    public static List<AndroidCert> Read(string apkPath, ZipArchive zip)
    {
        var certs = new List<AndroidCert>();
        foreach (var e in zip.Entries)
        {
            var n = e.FullName;
            if (!n.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || n.Count(c => c == '/') != 1) continue;
            if (!(n.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".EC", StringComparison.OrdinalIgnoreCase))) continue;
            if (e.Length > 1_000_000) continue;
            try
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var col = new X509Certificate2Collection();
                col.Import(ms.ToArray());
                // The signer is the leaf: the one that does not issue any other cert in the bundle.
                var leaf = col.Cast<X509Certificate2>().FirstOrDefault(c => !col.Cast<X509Certificate2>().Any(o => !ReferenceEquals(o, c) && o.Issuer == c.Subject)) ?? col.Cast<X509Certificate2>().FirstOrDefault();
                if (leaf is not null) certs.Add(ToCert("v1", leaf));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException) { }
        }

        try
        {
            foreach (var (scheme, der) in ReadSigningBlock(apkPath))
            {
                try { certs.Add(ToCert(scheme, new X509Certificate2(der))); }
                catch (CryptographicException) { certs.Add(new AndroidCert { Scheme = scheme, Subject = "?" }); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return certs;
    }

    public static AndroidCert ToCert(string scheme, X509Certificate2 c)
    {
        string key;
        try
        {
            using var rsa = c.GetRSAPublicKey();
            if (rsa is not null) key = "RSA " + rsa.KeySize;
            else
            {
                using var ec = c.GetECDsaPublicKey();
                key = ec is not null ? "EC " + ec.KeySize : c.PublicKey.Oid.FriendlyName ?? "?";
            }
        }
        catch (CryptographicException) { key = c.PublicKey.Oid.FriendlyName ?? "?"; }
        return new AndroidCert
        {
            Scheme = scheme,
            Subject = c.Subject,
            Issuer = c.Issuer,
            Sha256 = Convert.ToHexString(SHA256.HashData(c.RawData)),
            NotBefore = c.NotBefore.ToUniversalTime(),
            NotAfter = c.NotAfter.ToUniversalTime(),
            Algorithm = c.SignatureAlgorithm.FriendlyName ?? c.SignatureAlgorithm.Value ?? "",
            KeyInfo = key,
        };
    }

    /// <summary>Yields (scheme, first signer certificate DER) for each signature scheme block present.</summary>
    public static List<(string Scheme, byte[] Der)> ReadSigningBlock(string path)
    {
        var results = new List<(string, byte[])>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var cdOffset = FindCentralDirectoryOffset(fs);
        if (cdOffset < 32) return results;
        var tail = new byte[24];
        fs.Position = cdOffset - 24;
        fs.ReadExactly(tail);
        if (System.Text.Encoding.ASCII.GetString(tail, 8, 16) != "APK Sig Block 42") return results;
        var blockSize = BinaryPrimitives.ReadInt64LittleEndian(tail);
        if (blockSize < 24 || blockSize > 64 * 1024 * 1024 || blockSize + 8 > cdOffset) return results;
        var block = new byte[blockSize - 24];
        // Layout: [u64 size][pairs…][u64 size][magic]; size excludes the first u64, so pairs start at cdOffset - size.
        fs.Position = cdOffset - blockSize;
        fs.ReadExactly(block);

        var p = 0;
        while (p + 12 <= block.Length)
        {
            var len = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(p));
            if (len < 4 || p + 8 + len > block.Length) break;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(p + 8));
            var value = block.AsSpan(p + 12, (int)len - 4);
            var scheme = id switch { V2Id => "v2", V3Id => "v3", V31Id => "v3.1", SourceStampId => "stamp", _ => null };
            if (scheme is not null && scheme != "stamp")
            {
                var der = FirstCert(value);
                if (der is not null) results.Add((scheme, der));
            }
            p += 8 + (int)len;
        }
        return results;
    }

    // value: [signers: len-prefixed seq of len-prefixed signer]; signer: [signed data (len-prefixed)] …;
    // signed data: [digests (len-prefixed seq)] [certificates (len-prefixed seq of len-prefixed DER)] …
    private static byte[]? FirstCert(ReadOnlySpan<byte> v)
    {
        try
        {
            var signers = LenPrefixed(v, 0, out _);
            var signer = LenPrefixed(signers, 0, out _);
            var signedData = LenPrefixed(signer, 0, out _);
            _ = LenPrefixed(signedData, 0, out var afterDigests);
            var certs = LenPrefixed(signedData, afterDigests, out _);
            var cert = LenPrefixed(certs, 0, out _);
            return cert.Length > 0 ? cert.ToArray() : null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static ReadOnlySpan<byte> LenPrefixed(ReadOnlySpan<byte> s, int at, out int next)
    {
        if (at + 4 > s.Length) throw new InvalidDataException();
        var len = BinaryPrimitives.ReadInt32LittleEndian(s[at..]);
        if (len < 0 || at + 4 + len > s.Length) throw new InvalidDataException();
        next = at + 4 + len;
        return s.Slice(at + 4, len);
    }

    private static long FindCentralDirectoryOffset(FileStream fs)
    {
        var len = fs.Length;
        var scan = (int)Math.Min(len, 65557);
        var buf = new byte[scan];
        fs.Position = len - scan;
        fs.ReadExactly(buf);
        for (var i = scan - 22; i >= 0; i--)
        {
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
                return BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 16));
        }
        return -1;
    }
}
