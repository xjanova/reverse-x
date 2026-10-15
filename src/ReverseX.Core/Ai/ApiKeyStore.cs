using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA1416 // DPAPI is Windows-only by design; the app is a Windows app
namespace ReverseX.Core.Ai;

/// <summary>
/// The user's Anthropic API key, encrypted with Windows DPAPI (CurrentUser) in %LOCALAPPDATA%\ReverseX\apikey.bin.
/// Only a key the user typed into Reverse X is ever used: nothing is read from environment variables or other programs.
/// The key is never logged and never written in clear text.
/// </summary>
public sealed class ApiKeyStore
{
    // Extra entropy ties the blob to this app (another DPAPI user-level program cannot decrypt it by default call).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReverseX.ApiKey.v1");
    private readonly string _file;

    public ApiKeyStore(string? file = null) => _file = file ?? Path.Combine(AppPaths.Root, "apikey.bin");

    /// <summary>The store for another vendor's key: same folder, file apikey-{provider}.bin (Anthropic keeps the original file).</summary>
    public ApiKeyStore ForProvider(string providerId) =>
        providerId == AiProviders.DefaultId ? this
        : new ApiKeyStore(Path.Combine(Path.GetDirectoryName(_file)!, "apikey-" + providerId + ".bin"));

    public string FilePath => _file;

    public bool HasKey => File.Exists(_file);

    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("empty key", nameof(key));
        var plain = Encoding.UTF8.GetBytes(key);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllBytes(_file, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The decrypted key, or null when none is stored or it cannot be decrypted (other user / corrupt file).</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("API key file could not be read"); // never include key material
            return null;
        }
    }

    public void Delete()
    {
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("Could not delete key file", ex); }
    }

    /// <summary>"sk-ant-…abcd": enough to recognise a key without exposing it.</summary>
    public static string Mask(string key) =>
        key.Length <= 12 ? new string('•', key.Length) : key[..7] + "…" + key[^4..];
}
