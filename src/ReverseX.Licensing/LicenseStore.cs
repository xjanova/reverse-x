using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReverseX.Licensing;

/// <summary>What the license service remembers between restarts.</summary>
internal sealed class LicenseState
{
    [JsonPropertyName("machine")] public string MachineId { get; set; } = "";
    /// <summary>First successful register-device (server time). Null = never registered.</summary>
    [JsonPropertyName("registered_at")] public DateTime? RegisteredAt { get; set; }
    [JsonPropertyName("blocked")] public bool DeviceBlocked { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    /// <summary>UTC; null = lifetime.</summary>
    [JsonPropertyName("expires")] public DateTime? ExpiresAt { get; set; }
    /// <summary>active / expired / revoked / other_machine.</summary>
    [JsonPropertyName("key_state")] public string KeyState { get; set; } = "";
    /// <summary>Last time the server confirmed the key (server time, UTC).</summary>
    [JsonPropertyName("verified")] public DateTime? VerifiedAt { get; set; }
    /// <summary>/demo is asked once per install (the server treats repeats as abuse).</summary>
    [JsonPropertyName("trial_tried")] public bool TrialTried { get; set; }
    /// <summary>Server clock − PC clock at the last definite answer (seconds).</summary>
    [JsonPropertyName("offset")] public double ServerOffsetSeconds { get; set; }
    /// <summary>Forward-only watermark of trusted time; a clock that goes behind it was turned back.</summary>
    [JsonPropertyName("last_seen")] public DateTime? LastSeenUtc { get; set; }
}

internal enum StoreRead { Missing, Ok, Tampered }

/// <summary>
/// %LOCALAPPDATA%\ReverseX\license.json, sealed with an HMAC keyed by this PC's own ids (MachineGuid + SMBIOS UUID + user).
/// Anti X keeps its file in an Administrators-only folder; a per-user app cannot, so an edited, copied or
/// hand-written file reads as <see cref="StoreRead.Tampered"/> and counts as "never registered" (owner rule 2026-10-10).
/// </summary>
internal sealed class LicenseStore(string file, string sealSecret)
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Compact = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string File { get; } = file;

    public StoreRead Load(out LicenseState? state)
    {
        state = null;
        try
        {
            if (!System.IO.File.Exists(File)) return StoreRead.Missing;
            var root = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject;
            if (root?["state"] is not JsonObject body || root["seal"]?.GetValue<string>() is not { } seal) return StoreRead.Tampered;
            if ((root["v"]?.GetValue<int>() ?? 0) != FormatVersion) return StoreRead.Tampered;
            var parsed = body.Deserialize<LicenseState>(Compact);
            if (parsed is null) return StoreRead.Tampered;
            // The seal covers our own serialisation of what was read, so any edited, added or reordered value breaks it.
            var expected = Seal(parsed);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(seal))) return StoreRead.Tampered;
            state = parsed;
            return StoreRead.Ok;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return StoreRead.Tampered;
        }
    }

    public void Save(LicenseState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        var root = new JsonObject
        {
            ["v"] = FormatVersion,
            ["state"] = JsonSerializer.SerializeToNode(state, Compact),
            ["seal"] = Seal(state),
        };
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, root.ToJsonString(Indented), Encoding.UTF8);
        System.IO.File.Move(tmp, File, overwrite: true);
    }

    private string Seal(LicenseState state)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("reversex-license-seal-v1|" + sealSecret));
        return Convert.ToBase64String(HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(state, Compact)));
    }
}
