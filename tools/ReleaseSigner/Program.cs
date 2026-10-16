// Signs a published Reverse X folder so the in-app updater will install it (same tool as Anti X / WinXTools).
//
//   dotnet run --project tools/ReleaseSigner -c Release -- <app folder> <version>
//   dotnet run --project tools/ReleaseSigner -c Release -- keygen <private key file>
//
// The private key (base64 PKCS#8, ECDSA P-256) comes from REVERSEX_UPDATE_SIGNING_KEY — in CI, the
// repository secret of the same name. It is never stored in this repository. Writes update-manifest.json
// (path, size, SHA-256 of every file) and update-manifest.sig into the folder, then checks the result with
// the app's own verifier against the public key compiled into Reverse X, so a wrong secret fails the
// release instead of publishing an update nobody could install.

using System.Security.Cryptography;
using System.Text.Json;
using ReverseX.Licensing;

const string SecretName = "REVERSEX_UPDATE_SIGNING_KEY";

if (args.Length == 2 && args[0] == "keygen")
{
    if (File.Exists(args[1]))
    {
        Console.Error.WriteLine(args[1] + " already exists — refusing to overwrite a signing key.");
        return 1;
    }
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    Console.WriteLine("Private key written to " + Path.GetFullPath(args[1]) + $" — keep a backup; set it as the {SecretName} secret.");
    Console.WriteLine("Public key (UpdatePackageVerifier.ReleasePublicKey):");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (args.Length == 2 && args[0] == "sign-file")
{
    // detached signature of the whole release zip (<file>.sig, base64 IEEE P1363) so it can be checked before unpacking
    var key = Environment.GetEnvironmentVariable(SecretName);
    if (string.IsNullOrWhiteSpace(key) || !File.Exists(args[1]))
    {
        Console.Error.WriteLine($"{SecretName} is not set or {args[1]} does not exist.");
        return 1;
    }
    using var ec = ECDsa.Create();
    try { ec.ImportPkcs8PrivateKey(Convert.FromBase64String(key.Trim()), out _); }
    catch (Exception ex) when (ex is FormatException or CryptographicException)
    {
        Console.Error.WriteLine($"{SecretName} is not a base64 PKCS#8 ECDSA key.");
        return 1;
    }
    var bytes = File.ReadAllBytes(args[1]);
    var sig = ec.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    using var pub = ECDsa.Create();
    pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePackageVerifier.ReleasePublicKey), out _);
    if (!pub.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    {
        Console.Error.WriteLine("The secret is not the private half of UpdatePackageVerifier.ReleasePublicKey.");
        return 1;
    }
    File.WriteAllText(args[1] + ".sig", Convert.ToBase64String(sig));
    File.WriteAllText(args[1] + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "  " + Path.GetFileName(args[1]) + "\n");
    Console.WriteLine($"Signed {Path.GetFileName(args[1])} → .sig + .sha256");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ReleaseSigner <app folder> <version> | ReleaseSigner keygen <file>");
    return 2;
}

var folder = Path.GetFullPath(args[0]);
var version = args[1].Trim().TrimStart('v', 'V');
if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"Folder not found: {folder}");
    return 2;
}

var secret = Environment.GetEnvironmentVariable(SecretName);
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine($"{SecretName} is not set. Refusing to publish an update Reverse X cannot verify.");
    return 1;
}

var manifestPath = Path.Combine(folder, UpdatePackageVerifier.ManifestFileName);
var signaturePath = Path.Combine(folder, UpdatePackageVerifier.SignatureFileName);
File.Delete(manifestPath);
File.Delete(signaturePath);

var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.Ordinal)
    .Select(p =>
    {
        using var stream = File.OpenRead(p);
        return new
        {
            path = Path.GetRelativePath(folder, p).Replace('\\', '/'),
            size = new FileInfo(p).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
        };
    })
    .ToList();

if (!files.Any(f => f.path.Equals(UpdatePackageVerifier.MainExe, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine(UpdatePackageVerifier.MainExe + " is not in the folder.");
    return 1;
}

var manifest = JsonSerializer.SerializeToUtf8Bytes(new { product = UpdatePackageVerifier.ProductSlug, version, files }, new JsonSerializerOptions { WriteIndented = true });
byte[] signature;
using (var key = ECDsa.Create())
{
    try { key.ImportPkcs8PrivateKey(Convert.FromBase64String(secret.Trim()), out _); }
    catch (Exception ex) when (ex is FormatException or CryptographicException)
    {
        Console.Error.WriteLine($"{SecretName} is not a base64 PKCS#8 ECDSA key.");
        return 1;
    }
    signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
File.WriteAllBytes(manifestPath, manifest);
File.WriteAllText(signaturePath, Convert.ToBase64String(signature));

// ตรวจแบบเดียวกับที่โปรแกรมจะตรวจ บนสำเนา
var check = Path.Combine(Path.GetTempPath(), "reversex-sign-check-" + Guid.NewGuid().ToString("N"));
try
{
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(check, Path.GetRelativePath(folder, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    var verdict = UpdatePackageVerifier.Verify(check, version, "0.0.0");
    if (verdict != UpdatePackageVerifier.Result.Valid)
    {
        Console.Error.WriteLine($"The signed folder does not verify with the public key built into Reverse X ({verdict}). " +
                                "Is the secret the private half of UpdatePackageVerifier.ReleasePublicKey?");
        return 1;
    }
}
finally
{
    try { Directory.Delete(check, recursive: true); } catch (IOException) { /* ignore */ }
}

Console.WriteLine($"Signed {files.Count} files for Reverse X {version}.");
return 0;
