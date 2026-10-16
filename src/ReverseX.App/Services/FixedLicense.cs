using ReverseX.Licensing;

namespace ReverseX.App.Services;

/// <summary>A license that never talks to a server: tests, screenshots, and view models built without the real service.</summary>
internal sealed class FixedLicenseSource(LicenseStatus status) : ILicenseSource
{
    public LicenseStatus Status { get; private set; } = status;
    public event Action? Changed;

    public static FixedLicenseSource Pro() => new(new LicenseStatus { Tier = LicenseTier.Pro, Reason = LicenseReason.ProLifetime });

    public void Set(LicenseStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public Task<LicenseActionResult> RefreshAsync() => Task.FromResult(new LicenseActionResult(true, LicenseActionCode.Ok));
    public Task<LicenseActionResult> ActivateAsync(string licenseKey, bool moveFromOtherPc) => Task.FromResult(new LicenseActionResult(false, LicenseActionCode.Offline));
    public Task<LicenseActionResult> DeactivateAsync() => Task.FromResult(new LicenseActionResult(false, LicenseActionCode.Offline));
}

internal sealed class FixedUpdateSource(UpdateStatus status) : IUpdateSource
{
    public UpdateStatus Status { get; private set; } = status;
    public event Action? Changed;

    public void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
    public Updater.Outcome? LastOutcome => null;
    public Task<UpdateResult> CheckAsync() => Task.FromResult(new UpdateResult(true));
    public Task<UpdateResult> InstallAsync() => Task.FromResult(new UpdateResult(false, UpdateError.NothingToInstall));
}

internal static class AppVersion
{
    /// <summary>"0.1.0" (CI sets it from the tag with -p:Version=X.Y.Z); build metadata after '+' is dropped.</summary>
    public static string Current { get; } =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(AppVersion).Assembly)?.InformationalVersion
         ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];
}
