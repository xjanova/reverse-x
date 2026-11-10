using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Engines;
using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

/// <summary>
/// Installs the pinned engines REA needs (Ghidra 12.1.4, JDK 21, JADX MCP) into the app's data folder. One instance is shared
/// by the Home tools card and the Binary page's no-engine card, so both show the same offer and progress. Never throws to the UI.
/// </summary>
public sealed class EngineSetupViewModel : ObservableObject
{
    private readonly EngineInstaller _installer;
    private readonly Func<IReadOnlyList<EngineSpec>> _findMissing;
    private readonly Func<Task>? _afterInstall;
    private IReadOnlyList<EngineSpec> _missing = Array.Empty<EngineSpec>();
    private IReadOnlyList<EngineSpec> _run = Array.Empty<EngineSpec>();
    private long _needBytes;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private EngineInstallProgress? _last;
    private string? _resultKey;
    private object[] _resultArgs = Array.Empty<object>();
    private bool _resultIsError;

    /// <param name="afterInstall">Runs after an attempt that installed at least one engine: kept REA processes must restart to see it.</param>
    public EngineSetupViewModel(Func<Task>? afterInstall = null, EngineInstaller? installer = null, Func<IReadOnlyList<EngineSpec>>? findMissing = null)
    {
        _afterInstall = afterInstall;
        _installer = installer ?? new EngineInstaller();
        _findMissing = findMissing ?? (() => EngineCatalog.MissingFrom(EngineLocator.ResolveDefault()));
        InstallCommand = new RelayCommand(() => { _ = InstallAsync(); }, () => !_busy && _missing.Count > 0);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        Loc.Changed += RefreshText;
    }

    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    public bool HasMissing => _missing.Count > 0;
    public bool IsBusy => _busy;
    public bool ShowOffer => HasMissing && !_busy;

    public string OfferText => Loc.Format("Eng.Offer", string.Join(" · ", _missing.Select(Label)),
        ByteSize.Format(_missing.Sum(s => s.Size)), ByteSize.Format(_needBytes));

    public string ProgressText
    {
        get
        {
            if (_last is not { } p) return Loc.Get("Eng.Starting");
            var name = Label(p.Engine);
            return p.Phase switch
            {
                EngineInstallPhase.Download => Loc.Format("Eng.Downloading", name, p.Index + 1, p.Count, ByteSize.Format(p.Done), ByteSize.Format(p.Total)),
                EngineInstallPhase.Verify => Loc.Format("Eng.Verifying", name, p.Index + 1, p.Count),
                EngineInstallPhase.Extract => Loc.Format("Eng.Extracting", name, p.Index + 1, p.Count, Percent(p.Done, p.Total)),
                _ => Loc.Format("Eng.InstalledOne", name, p.Index + 1, p.Count),
            };
        }
    }

    /// <summary>0–100 over the whole run, weighted by download size (Ghidra dominates); unpacking counts as the last 40 % of each engine.</summary>
    public double ProgressValue
    {
        get
        {
            if (_last is not { } p || _run.Count == 0 || p.Index >= _run.Count) return 0;
            double total = _run.Sum(s => s.Size);
            if (total <= 0) return 0;
            var ratio = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : 0;
            var part = p.Phase switch
            {
                EngineInstallPhase.Download => 0.6 * ratio,
                EngineInstallPhase.Verify => 0.6,
                EngineInstallPhase.Extract => 0.6 + 0.4 * ratio,
                _ => 1.0,
            };
            var done = _run.Take(p.Index).Sum(s => s.Size) + part * _run[p.Index].Size;
            return Math.Clamp(100.0 * done / total, 0, 100);
        }
    }

    public string? ResultText => _resultKey is null ? null : Loc.Format(_resultKey, _resultArgs);
    public bool HasResult => _resultKey is not null;
    public bool ResultIsError => _resultIsError;

    /// <summary>Re-reads which engines are missing (a few file checks, off the UI thread).</summary>
    public async Task RefreshAsync()
    {
        var (missing, need) = await Task.Run(() =>
        {
            var m = _findMissing();
            return (m, _installer.RequiredFreeBytesFor(m));
        }).ConfigureAwait(true);
        _missing = missing;
        _needBytes = need;
        Raise(nameof(HasMissing)); Raise(nameof(ShowOffer)); Raise(nameof(OfferText));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task InstallAsync()
    {
        if (_busy || _missing.Count == 0) return;
        var which = _run = _missing;
        var cts = _cts = new CancellationTokenSource();
        _last = null;
        SetResult(null);
        SetBusy(true);
        var progress = new Progress<EngineInstallProgress>(p =>
        {
            if (!_busy) return;
            _last = p;
            Raise(nameof(ProgressText)); Raise(nameof(ProgressValue));
        });
        try
        {
            await _installer.InstallAsync(which, progress, cts.Token).ConfigureAwait(true);
            SetResult("Eng.Done");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetResult("Eng.Cancelled");
        }
        catch (EngineInstallException ex)
        {
            AppPaths.Log("Engine install failed", ex);
            var name = ex.Engine is null ? "" : Label(ex.Engine);
            switch (ex.Kind)
            {
                case EngineInstallError.NoSpace:
                    SetResult("Eng.Err.NoSpace", true, ByteSize.Format(ex.RequiredBytes), ByteSize.Format(ex.AvailableBytes));
                    break;
                case EngineInstallError.Io:
                    SetResult("Eng.Err.Io", true, _installer.EnginesDir);
                    break;
                default:
                    SetResult(ex.Kind switch
                    {
                        EngineInstallError.Network => "Eng.Err.Network",
                        EngineInstallError.HashMismatch => "Eng.Err.Hash",
                        EngineInstallError.BadArchive => "Eng.Err.Archive",
                        EngineInstallError.Busy => "Eng.Err.Busy",
                        _ => "Eng.Err.Failed",
                    }, true, name);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Engine install crashed", ex);
            SetResult("Eng.Err.Failed", true, "");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            _last = null;
            SetBusy(false);
        }

        // Engines finished before a failure stay installed, so compare what is still missing rather than trusting the outcome.
        try
        {
            await RefreshAsync().ConfigureAwait(true);
            if (_missing.Count < which.Count && _afterInstall is not null) await _afterInstall().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("After engine install", ex);
        }
    }

    /// <summary>App is closing: stop a running download; the installer removes its .part and staging folders.</summary>
    public void Cancel() => _cts?.Cancel();

    private static string Label(EngineSpec s) => s.Kind switch
    {
        EngineKind.Ghidra => "Ghidra " + s.Version,
        EngineKind.Jdk => "JDK " + s.Version.Split('.')[0],
        _ => "JADX MCP " + s.Version,
    };

    private static string Percent(long done, long total) =>
        total > 0 ? Math.Clamp(done * 100 / total, 0, 100).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%" : "…";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Raise(nameof(IsBusy)); Raise(nameof(ShowOffer)); Raise(nameof(ProgressText)); Raise(nameof(ProgressValue));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetResult(string? key, bool error = false, params object[] args)
    {
        _resultKey = key;
        _resultArgs = args;
        _resultIsError = error;
        Raise(nameof(ResultText)); Raise(nameof(HasResult)); Raise(nameof(ResultIsError));
    }

    private void RefreshText()
    {
        Raise(nameof(OfferText)); Raise(nameof(ProgressText)); Raise(nameof(ResultText));
    }
}
