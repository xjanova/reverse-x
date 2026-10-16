namespace ReverseX.Licensing;

public enum UpdateState { Idle, Checking, Downloading, Verifying, Installing, Failed }

/// <summary>Why an update was not checked or not installed; the app turns it into Thai or English text.</summary>
public enum UpdateError
{
    None,
    /// <summary>xman4289.com could not be reached, or answered 5xx/429.</summary>
    Offline,
    NothingToInstall,
    NotXmanUrl,
    NoChecksum,
    Redirected,
    HttpError,
    NotAPackage,
    SizeMismatch,
    TooLarge,
    /// <summary>The download stopped early (fewer bytes than announced, or the connection dropped).</summary>
    Incomplete,
    HashMismatch,
    BadArchive,
    NoAppFolder,
    Unsigned,
    BadSignature,
    WrongVersion,
    FilesMismatch,
    /// <summary>The program folder is not writable by this user (e.g. under Program Files).</summary>
    InstallDirReadOnly,
    IoError,
}

public sealed class UpdateStatus
{
    public string Current { get; init; } = "";
    public string? Latest { get; init; }
    public bool Available { get; init; }
    public UpdateState State { get; init; }
    public UpdateError Error { get; init; }
    /// <summary>0..1 while downloading.</summary>
    public double Progress { get; init; }
    public DateTime? CheckedAt { get; init; }
    /// <summary>Technical detail (HTTP code, exception text) for the log and a small second line.</summary>
    public string? Detail { get; init; }
}

public sealed record UpdateResult(bool Ok, UpdateError Error = UpdateError.None, string? Detail = null);

/// <summary>What the settings page needs from the updater (the real service, or a fixed one for screenshots).</summary>
public interface IUpdateSource
{
    UpdateStatus Status { get; }
    /// <summary>Raised on any thread when <see cref="Status"/> changed.</summary>
    event Action? Changed;
    Task<UpdateResult> CheckAsync();
    /// <summary>Downloads, verifies and starts the installer. On success the app must exit right away so the files can be replaced.</summary>
    Task<UpdateResult> InstallAsync();
    /// <summary>What the previous update left behind (shown once), or null.</summary>
    Updater.Outcome? LastOutcome { get; }
}
