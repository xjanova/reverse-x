using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Licensing;

namespace ReverseX.App.ViewModels;

/// <summary>
/// License + update state for the settings page, the register-this-PC screen and the Pro lock on the AI page.
/// Free: every analysis page. Pro (and the 14-day trial): the AI assistant and code written into the user's project.
/// </summary>
public sealed class LicenseViewModel : ObservableObject
{
    private readonly ILicenseSource _lic;
    private readonly IUpdateSource _upd;
    private string _keyInput = "";
    private string _notice = "";
    private bool _noticeError;
    private bool _busy;
    private bool _canMove;
    private string _updateNotice = "";
    private bool _updateNoticeError;

    public LicenseViewModel(ILicenseSource license, IUpdateSource updates)
    {
        _lic = license;
        _upd = updates;
        ActivateCommand = new RelayCommand(() => _ = ActivateAsync(false), () => !_busy);
        MoveCommand = new RelayCommand(() => _ = ActivateAsync(true), () => !_busy && _canMove);
        DeactivateCommand = new RelayCommand(() => _ = DeactivateAsync(), () => !_busy && _lic.Status.HasPaidKey);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_busy);
        BuyCommand = new RelayCommand(() => OpenUrl(_lic.Status.PurchaseUrl));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        CheckUpdateCommand = new RelayCommand(() => _ = CheckUpdateAsync(), () => _upd.Status.State is UpdateState.Idle or UpdateState.Failed);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => _upd.Status.Available && _upd.Status.State is UpdateState.Idle or UpdateState.Failed);
        _lic.Changed += () => Post(RaiseLicense);
        _upd.Changed += () => Post(RaiseUpdate);
        Loc.Changed += () => { RaiseLicense(); RaiseUpdate(); };
        if (_upd.LastOutcome is { } last)
        {
            _updateNotice = last.Ok ? Loc.Format("Upd.Last.Ok", last.Version) : Loc.Format("Upd.Last.Fail", last.Version, last.Detail);
            _updateNoticeError = !last.Ok;
        }
    }

    /// <summary>The user chose "exit" on the register screen, or an update is ready to be put in place.</summary>
    public event Action? ExitRequested;
    /// <summary>Pro features opened or closed.</summary>
    public event Action? ProChanged;

    public ICommand ActivateCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand BuyCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    private LicenseStatus S => _lic.Status;

    public bool IsPro => S.IsPro;
    public bool IsRegistered => S.IsRegistered;
    public bool ShowGate => !S.IsRegistered;
    public bool IsTrial => S.Tier == LicenseTier.Trial;
    public bool HasPaidKey => S.HasPaidKey;
    public bool ShowBuy => S.Tier != LicenseTier.Pro;
    public string Tier => S.Tier.ToString();
    public string TierText => Loc.Get("Lic.Tier." + S.Tier);

    public string ReasonText => S.Reason switch
    {
        LicenseReason.TrialActive => Loc.Format("Lic.Reason.TrialActive", S.DaysLeft ?? 0),
        LicenseReason.ProUntil => Loc.Format("Lic.Reason.ProUntil", Date(S.ExpiresAt), S.DaysLeft ?? 0),
        _ => Loc.Get("Lic.Reason." + S.Reason),
    };

    public string NoteText => S.Note switch
    {
        LicenseNote.None => "",
        LicenseNote.RegisterFailed => Loc.Format("Lic.Note.RegisterFailed", S.ServerMessage ?? "—"),
        LicenseNote.Offline when !S.IsRegistered => Loc.Get("Lic.Note.OfflineFirst"),
        _ => Loc.Get("Lic.Note." + S.Note),
    };
    public bool HasNote => NoteText.Length > 0;

    public int TrialDaysLeft => S.DaysLeft ?? 0;
    public string TrialDaysText => Loc.Format("Lic.TrialDays", TrialDaysLeft);
    public string KeyText => S.KeyMasked is { } k && !IsTrial ? k : Loc.Get("Lic.NoKey");
    public string VerifiedText => S.LastVerifiedAt is { } v ? Loc.Format("Lic.Verified", Date(v)) : Loc.Get("Lic.NotVerified");

    public string KeyInput { get => _keyInput; set => Set(ref _keyInput, value); }
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => _notice.Length > 0;
    public bool NoticeError { get => _noticeError; private set => Set(ref _noticeError, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool CanMove { get => _canMove; private set => Set(ref _canMove, value); }

    // ---------------------------------------------------------------- updates

    private UpdateStatus U => _upd.Status;
    public string VersionText => Loc.Format("Upd.Current", U.Current);
    public bool UpdateAvailable => U.Available && U.State is UpdateState.Idle or UpdateState.Failed;
    public bool IsDownloading => U.State == UpdateState.Downloading;
    public double DownloadPercent => U.Progress * 100;

    public string UpdateText => U.State switch
    {
        UpdateState.Checking => Loc.Get("Upd.Checking"),
        UpdateState.Downloading => Loc.Format("Upd.Downloading", (int)(U.Progress * 100)),
        UpdateState.Verifying => Loc.Get("Upd.Verifying"),
        UpdateState.Installing => Loc.Get("Upd.Installing"),
        _ when U.Available => Loc.Format("Upd.Available", U.Latest ?? "?"),
        _ when U.Error == UpdateError.Offline => Loc.Get("Upd.Err.Offline"),
        _ when U.CheckedAt is null => Loc.Get("Upd.Never"),
        _ => Loc.Get("Upd.UpToDate"),
    };

    public string CheckedText => U.CheckedAt is { } c ? Loc.Format("Upd.Checked", Date(c, withTime: true)) : "";

    public string UpdateNotice { get => _updateNotice; private set { if (Set(ref _updateNotice, value)) Raise(nameof(HasUpdateNotice)); } }
    public bool HasUpdateNotice => _updateNotice.Length > 0;
    public bool UpdateNoticeError { get => _updateNoticeError; private set => Set(ref _updateNoticeError, value); }

    /// <summary>Thai/English sentence for why an update was not installed.</summary>
    public static string ErrorText(UpdateError e) => Loc.Get("Upd.Err." + e);

    // ---------------------------------------------------------------- actions

    private async Task RefreshAsync()
    {
        IsBusy = true;
        Notice = Loc.Get("Lic.Busy");
        NoticeError = false;
        try
        {
            var r = await _lic.RefreshAsync().ConfigureAwait(true);
            Show(r);
        }
        catch (Exception ex)
        {
            AppPaths.Log("License refresh failed", ex);
            Show(new LicenseActionResult(false, LicenseActionCode.Offline));
        }
        finally { IsBusy = false; }
    }

    private async Task ActivateAsync(bool move)
    {
        IsBusy = true;
        Notice = Loc.Get("Lic.Busy");
        NoticeError = false;
        try
        {
            var r = await _lic.ActivateAsync(_keyInput, move).ConfigureAwait(true);
            CanMove = r.Code == LicenseActionCode.OtherDevice;
            if (r.Ok) KeyInput = "";
            Show(r);
        }
        catch (Exception ex)
        {
            AppPaths.Log("License activation failed", ex);
            Show(new LicenseActionResult(false, LicenseActionCode.Offline));
        }
        finally { IsBusy = false; }
    }

    private async Task DeactivateAsync()
    {
        IsBusy = true;
        try { Show(await _lic.DeactivateAsync().ConfigureAwait(true)); }
        catch (Exception ex)
        {
            AppPaths.Log("License deactivation failed", ex);
            Show(new LicenseActionResult(false, LicenseActionCode.Offline));
        }
        finally { IsBusy = false; }
    }

    private void Show(LicenseActionResult r)
    {
        Notice = r.Code == LicenseActionCode.ServerError
            ? Loc.Format("Lic.Act.ServerError", r.ServerMessage ?? "—")
            : r.Code == LicenseActionCode.NeedRegistration && r.ServerMessage is { Length: > 0 } m
                ? Loc.Format("Lic.Note.RegisterFailed", m)
                : Loc.Get("Lic.Act." + r.Code);
        NoticeError = !r.Ok;
        RaiseLicense();
    }

    private async Task CheckUpdateAsync()
    {
        UpdateNotice = "";
        try
        {
            var r = await _upd.CheckAsync().ConfigureAwait(true);
            if (!r.Ok) { UpdateNotice = ErrorText(r.Error); UpdateNoticeError = true; }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update check failed", ex);
            UpdateNotice = ErrorText(UpdateError.Offline);
            UpdateNoticeError = true;
        }
        RaiseUpdate();
    }

    private async Task InstallUpdateAsync()
    {
        UpdateNotice = "";
        try
        {
            var r = await _upd.InstallAsync().ConfigureAwait(true);
            if (r.Ok)
            {
                UpdateNotice = Loc.Get("Upd.Restarting");
                UpdateNoticeError = false;
                ExitRequested?.Invoke();
                return;
            }
            UpdateNotice = ErrorText(r.Error);
            UpdateNoticeError = true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update install failed", ex);
            UpdateNotice = ErrorText(UpdateError.IoError);
            UpdateNoticeError = true;
        }
        RaiseUpdate();
    }

    /// <summary>Shows a refusal the way the settings page does (screenshots).</summary>
    internal void ShowUpdateError(UpdateError e)
    {
        UpdateNotice = ErrorText(e);
        UpdateNoticeError = true;
    }

    internal void ShowUpdateNotice(string text, bool error)
    {
        UpdateNotice = text;
        UpdateNoticeError = error;
    }

    internal void ShowNotice(string text, bool error)
    {
        Notice = text;
        NoticeError = error;
    }

    private bool _lastPro;

    private void RaiseLicense()
    {
        foreach (var n in new[] { nameof(IsPro), nameof(IsRegistered), nameof(ShowGate), nameof(IsTrial), nameof(HasPaidKey), nameof(ShowBuy), nameof(Tier),
                     nameof(TierText), nameof(ReasonText), nameof(NoteText), nameof(HasNote), nameof(TrialDaysLeft), nameof(TrialDaysText), nameof(KeyText), nameof(VerifiedText) })
            Raise(n);
        CommandManager.InvalidateRequerySuggested();
        if (_lastPro != IsPro)
        {
            _lastPro = IsPro;
            ProChanged?.Invoke();
        }
    }

    private void RaiseUpdate()
    {
        foreach (var n in new[] { nameof(VersionText), nameof(UpdateAvailable), nameof(IsDownloading), nameof(DownloadPercent), nameof(UpdateText), nameof(CheckedText) })
            Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Date(DateTime? utc, bool withTime = false)
    {
        if (utc is not { } d) return "—";
        var culture = Loc.Current == UiPrefs.Thai ? new CultureInfo("th-TH") : CultureInfo.GetCultureInfo("en-GB");
        return d.ToLocalTime().ToString(withTime ? "d MMM yyyy HH:mm" : "d MMM yyyy", culture);
    }

    private static void OpenUrl(string url)
    {
        // เปิดเฉพาะหน้าบน xman4289.com
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("xman4289.com", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppPaths.Log("Could not open the store page", ex); }
    }

    private static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    /// <summary>Starts tracking Pro (call once after construction, when listeners are attached).</summary>
    internal void Prime() => _lastPro = IsPro;
}
