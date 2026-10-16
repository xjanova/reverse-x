namespace ReverseX.Licensing;

/// <summary>Unregistered = nothing runs (owner rule). Free = analysis pages. Trial and Pro = everything.</summary>
public enum LicenseTier { Unregistered, Free, Trial, Pro }

/// <summary>Why the tier is what it is; the app turns it into Thai or English text.</summary>
public enum LicenseReason
{
    NotRegistered,
    Blocked,
    /// <summary>The local license file was edited, copied from another PC or hand-written.</summary>
    TamperedFile,
    FreeNoKey,
    TrialActive,
    TrialEnded,
    ProLifetime,
    ProUntil,
    ProExpired,
    NotProKey,
    /// <summary>A paid key not confirmed by xman for more than 30 days.</summary>
    OfflineTooLong,
    Revoked,
    OtherMachine,
    KeyExpired,
    /// <summary>The PC clock is behind the last time we saw (turned back) — paid/trial features wait for xman.</summary>
    ClockBack,
}

/// <summary>Side remark on the last round with the server.</summary>
public enum LicenseNote { None, Offline, ValidateLater, RegisterFailed }

public sealed class LicenseStatus
{
    public LicenseTier Tier { get; init; }
    public LicenseReason Reason { get; init; }
    public LicenseNote Note { get; init; }
    /// <summary>The server's own words for a failed registration (shown as-is).</summary>
    public string? ServerMessage { get; init; }
    public int? DaysLeft { get; init; }
    public string? KeyMasked { get; init; }
    public string? LicenseType { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastVerifiedAt { get; init; }
    public string PurchaseUrl { get; init; } = XmanApi.ProductPageUrl;

    public bool IsRegistered => Tier != LicenseTier.Unregistered;
    /// <summary>Pro features (AI assistant, code for the user's project) are open.</summary>
    public bool IsPro => Tier is LicenseTier.Trial or LicenseTier.Pro;
    /// <summary>A paid key sits on this PC (so it can be released for another one).</summary>
    public bool HasPaidKey { get; init; }
}

public enum LicenseActionCode
{
    Ok,
    Activated,
    Deactivated,
    BadFormat,
    /// <summary>The PC could not be registered (offline) — a key cannot be activated before that.</summary>
    NeedRegistration,
    Offline,
    NotProKey,
    OtherDevice,
    InvalidKey,
    KeyExpired,
    KeyRevoked,
    NoPaidKey,
    ServerError,
}

public sealed record LicenseActionResult(bool Ok, LicenseActionCode Code, string? ServerMessage = null);

/// <summary>What the app's screens need from the license (the real service, or a fixed one for screenshots).</summary>
public interface ILicenseSource
{
    LicenseStatus Status { get; }
    /// <summary>Raised on any thread when <see cref="Status"/> changed.</summary>
    event Action? Changed;
    Task<LicenseActionResult> RefreshAsync();
    Task<LicenseActionResult> ActivateAsync(string licenseKey, bool moveFromOtherPc);
    Task<LicenseActionResult> DeactivateAsync();
}
