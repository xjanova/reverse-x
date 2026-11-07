using System.IO;
using System.Globalization;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Analysis;
using ReverseX.Core.Decompile;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.App.ViewModels;

public enum RunState { Idle, Running, Ready, Failed }

/// <summary>What the pages need from REA. The default one starts a real REA process; the screenshot mode and tests can swap it.</summary>
public interface IAnalysisBackend
{
    Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct);
    /// <summary>Pseudo-C or disassembly of one native function; needs a deep engine (Ghidra) for this file.</summary>
    Task<NativeCode> NativeCodeAsync(string path, string procedure, NativeCodeView view, CancellationToken ct);
    Task<string> CallGraphAsync(string path, string address, CancellationToken ct);
    /// <summary>One APK class as Java; needs the JADX MCP engine.</summary>
    Task<AndroidClassCode> AndroidClassJavaAsync(string apk, string className, IProgress<(int Done, int Total)>? progress, CancellationToken ct);
}

public sealed class ReaAnalysisBackend : IAnalysisBackend, IAsyncDisposable
{
    // One REA process stays up for the native file on screen: Ghidra's import + auto-analysis then happens once for the listing
    // and every function the user opens afterwards, instead of once per click.
    private readonly TargetSessionCache _native = new(StartAsync, (rea, path, ct) => new AnalysisRunner(rea).OpenAsync(path, ct));
    // Same for the APK on the Android page: JADX loads the whole APK on its first call in a process, then answers each class quickly.
    private readonly TargetSessionCache _android = new(StartAsync);

    private static async Task<NodeInfo> NodeAsync(CancellationToken ct) =>
        await NodeProbe.FindAsync(ct).ConfigureAwait(false)
        ?? throw new AnalysisFailure(AnalysisFailureKind.NoNode, "node not found");

    private static async Task<IReaSession> StartAsync(CancellationToken ct) => new ReaSession(await NodeAsync(ct).ConfigureAwait(false));

    public async Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct)
    {
        if (kind is FileKind.Pe or FileKind.Elf or FileKind.MachO)
            return await OnNativeAsync(path, (rea, c) => new AnalysisRunner(rea).AnalyzeAsync(path, kind, progress, c), ct).ConfigureAwait(false);

        try
        {
            var node = await NodeAsync(ct).ConfigureAwait(false);
            await using var session = new ReaSession(node);
            var runner = new AnalysisRunner(session, ReaSession.ManagedMembersViaCli(node));
            return await runner.AnalyzeAsync(path, kind, progress, ct).ConfigureAwait(false);
        }
        catch (AnalysisFailure ex) when (kind == FileKind.DotNet)
        {
            // REA could not list the members (budget, no Node, REA down): C# does not need REA, so list them from metadata here.
            AppPaths.Log("REA could not list .NET members, using the local metadata list: " + path, ex);
            try { return await Task.Run(() => AnalysisRunner.LocalDotNetSnapshot(path, ct), ct).ConfigureAwait(false); }
            catch (DecompileFailure local)
            {
                AppPaths.Log("Local metadata list failed too: " + path, local);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }

    public Task<NativeCode> NativeCodeAsync(string path, string procedure, NativeCodeView view, CancellationToken ct) =>
        OnKeptAsync(_native, path, (rea, c) => new AnalysisRunner(rea).NativeCodeAsync(procedure, view, c), ct);

    public Task<string> CallGraphAsync(string path, string address, CancellationToken ct) =>
        OnKeptAsync(_native, path, (rea, c) => new AnalysisRunner(rea).CallGraphAsync(address, c), ct);

    public Task<AndroidClassCode> AndroidClassJavaAsync(string apk, string className, IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
        OnKeptAsync(_android, apk, (rea, c) => new AnalysisRunner(rea).AndroidClassJavaAsync(apk, className, progress, c), ct);

    private Task<T> OnNativeAsync<T>(string path, Func<IReaToolCaller, CancellationToken, Task<T>> work, CancellationToken ct) =>
        OnKeptAsync(_native, path, work, ct);

    /// <summary>Runs on a kept session; process-level failures become the same AnalysisFailure kinds the pages already map.</summary>
    private static async Task<T> OnKeptAsync<T>(TargetSessionCache cache, string path, Func<IReaToolCaller, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        try { return await cache.RunAsync(path, work, ct).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new AnalysisFailure(AnalysisFailureKind.Timeout, ex.Message); }
        catch (ReaException ex) { throw new AnalysisFailure(AnalysisFailureKind.ReaUnavailable, ex.Message); }
    }

    /// <summary>A newer engine became available (installed from the app): the kept processes still run without it.</summary>
    public async Task ResetSessionsAsync()
    {
        await _native.ResetAsync().ConfigureAwait(false);
        await _android.ResetAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _native.DisposeAsync().ConfigureAwait(false); }
        finally { await _android.DisposeAsync().ConfigureAwait(false); }
    }
}

/// <summary>Shared run logic: snapshot first, otherwise a cancellable background analysis with progress. Never throws to the UI.</summary>
public abstract class AnalysisPageBase : ObservableObject
{
    private readonly SnapshotStore _snapshots;
    private readonly Func<string, Task>? _openPath;
    private CancellationTokenSource? _cts;
    private int _runId;
    private RunState _state = RunState.Idle;
    private string _progressText = "";
    private double _progressValue;
    private bool _indeterminate = true;
    private string _errorText = "";
    private AnalysisSnapshot? _snapshot;

    protected AnalysisPageBase(IAnalysisBackend backend, SnapshotStore snapshots, Func<string, Task>? openPath)
    {
        Backend = backend;
        _snapshots = snapshots;
        _openPath = openPath;
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => State == RunState.Running);
        ReanalyzeCommand = new RelayCommand(() => { _ = RunAsync(force: true); }, () => State != RunState.Running && TargetPath is not null);
        Loc.Changed += OnLocChanged;
    }

    protected IAnalysisBackend Backend { get; }
    protected abstract string TitleKey { get; }
    protected abstract bool Accepts(AnalysisKind saved, FileKind target);
    protected AnalysisSnapshot? Snapshot => _snapshot;
    /// <summary>Separate snapshot file for pages that read the same source differently (see SnapshotStore.FileFor).</summary>
    protected virtual string? SnapshotSlot => null;

    protected virtual Task<AnalysisSnapshot> AnalyzeCoreAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct) =>
        Backend.AnalyzeAsync(path, kind, progress, ct);

    protected virtual string ErrorKeyFor(Exception ex) =>
        ex is AnalysisFailure f ? "Err." + (f.Kind == AnalysisFailureKind.Generic ? "Analysis" : f.Kind.ToString()) : "Err.Analysis";

    public string? TargetPath { get; private set; }
    public FileKind TargetKind { get; private set; }
    public string TargetName => TargetPath is null ? Loc.Get(TitleKey) : System.IO.Path.GetFileName(TargetPath.TrimEnd('\\', '/'));

    public ICommand CancelCommand { get; }
    public ICommand ReanalyzeCommand { get; }

    public RunState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsIdle)); Raise(nameof(IsRunning)); Raise(nameof(IsReady)); Raise(nameof(IsFailed));
            OnStateChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => State == RunState.Idle;
    public bool IsRunning => State == RunState.Running;
    public bool IsReady => State == RunState.Ready;
    public bool IsFailed => State == RunState.Failed;

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public bool ProgressIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    public string StatusLine
    {
        get
        {
            if (_snapshot is null) return "";
            var local = _snapshot.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
            var text = Loc.Format(FromSnapshot ? "Run.FromSnapshot" : "Run.Fresh", local);
            if (SnapshotStore.IsStale(_snapshot)) text += " · " + Loc.Get("Run.Stale");
            return text;
        }
    }

    public bool FromSnapshot { get; private set; }

    /// <summary>Loads the saved result when there is one (and the file is the same kind), otherwise analyzes.</summary>
    public async Task OpenAsync(string path, FileKind kind, bool force = false)
    {
        TargetPath = path;
        TargetKind = kind;
        Raise(nameof(TargetName));
        await RunAsync(force).ConfigureAwait(true);
    }

    private async Task RunAsync(bool force)
    {
        var path = TargetPath;
        if (path is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var id = ++_runId;

        if (!force)
        {
            var saved = await Task.Run(() => _snapshots.TryLoad(path, SnapshotSlot)).ConfigureAwait(true);
            if (id != _runId) return;
            if (saved is not null && Accepts(saved.Kind, TargetKind))
            {
                _snapshot = saved;
                FromSnapshot = true;
                ApplySnapshot(saved);
                State = RunState.Ready;
                Raise(nameof(StatusLine));
                return;
            }
        }

        ClearResult();
        _snapshot = null;
        ErrorText = "";
        ProgressText = Loc.Get("Run.Running");
        ProgressValue = 0;
        ProgressIndeterminate = true;
        State = RunState.Running;
        Raise(nameof(StatusLine));

        var progress = new Progress<AnalysisProgress>(p =>
        {
            if (id != _runId) return;
            ProgressText = Loc.Format("Step." + p.Step, p.Done, p.Total);
            ProgressIndeterminate = p.Total <= 0;
            ProgressValue = p.Total > 0 ? Math.Clamp(100.0 * p.Done / p.Total, 0, 100) : 0;
        });

        try
        {
            var snap = await Task.Run(() => AnalyzeCoreAsync(path, TargetKind, progress, cts.Token)).ConfigureAwait(true);
            if (id != _runId) return;
            try { await Task.Run(() => _snapshots.Save(snap, SnapshotSlot)).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppPaths.Log("Could not save snapshot for " + path, ex);
            }
            _snapshot = snap;
            FromSnapshot = false;
            ApplySnapshot(snap);
            State = RunState.Ready;
        }
        catch (OperationCanceledException)
        {
            if (id != _runId) return;
            ErrorText = Loc.Get("Run.Cancelled");
            State = RunState.Failed;
        }
        catch (Exception ex)
        {
            if (id != _runId) return;
            AppPaths.Log("Analysis failed: " + path, ex);
            ErrorText = Loc.Get(ErrorKeyFor(ex));
            State = RunState.Failed;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            Raise(nameof(StatusLine));
        }
    }

    protected abstract void ApplySnapshot(AnalysisSnapshot snapshot);
    protected abstract void ClearResult();
    protected virtual void OnLanguageChanged() { }
    protected virtual void OnStateChanged() { }

    private void OnLocChanged()
    {
        Raise(nameof(StatusLine));
        OnLanguageChanged();
    }

    protected Task OpenPathAsync(string path) => _openPath?.Invoke(path) ?? Task.CompletedTask;
}
