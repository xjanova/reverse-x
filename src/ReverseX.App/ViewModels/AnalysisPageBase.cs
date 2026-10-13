using System.IO;
using System.Globalization;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.App.ViewModels;

public enum RunState { Idle, Running, Ready, Failed }

/// <summary>What the pages need from REA. The default one starts a real REA process; the screenshot mode and tests can swap it.</summary>
public interface IAnalysisBackend
{
    Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct);
    Task<string> FunctionDossierAsync(string path, string procedure, CancellationToken ct);
    Task<string> CallGraphAsync(string path, string address, CancellationToken ct);
}

public sealed class ReaAnalysisBackend : IAnalysisBackend
{
    private static async Task<NodeInfo> NodeAsync(CancellationToken ct) =>
        await NodeProbe.FindAsync(ct).ConfigureAwait(false)
        ?? throw new AnalysisFailure(AnalysisFailureKind.NoNode, "node not found");

    public async Task<AnalysisSnapshot> AnalyzeAsync(string path, FileKind kind, IProgress<AnalysisProgress> progress, CancellationToken ct)
    {
        var node = await NodeAsync(ct).ConfigureAwait(false);
        await using var session = new ReaSession(node);
        var runner = new AnalysisRunner(session, ReaSession.ManagedMembersViaCli(node));
        return await runner.AnalyzeAsync(path, kind, progress, ct).ConfigureAwait(false);
    }

    public async Task<string> FunctionDossierAsync(string path, string procedure, CancellationToken ct)
    {
        var node = await NodeAsync(ct).ConfigureAwait(false);
        await using var session = new ReaSession(node);
        var runner = new AnalysisRunner(session);
        await runner.OpenAsync(path, ct).ConfigureAwait(false);
        return await runner.AnalyzeFunctionAsync(procedure, ct).ConfigureAwait(false);
    }

    public async Task<string> CallGraphAsync(string path, string address, CancellationToken ct)
    {
        var node = await NodeAsync(ct).ConfigureAwait(false);
        await using var session = new ReaSession(node);
        var runner = new AnalysisRunner(session);
        await runner.OpenAsync(path, ct).ConfigureAwait(false);
        return await runner.CallGraphAsync(address, ct).ConfigureAwait(false);
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
            var saved = await Task.Run(() => _snapshots.TryLoad(path)).ConfigureAwait(true);
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
            var snap = await Task.Run(() => Backend.AnalyzeAsync(path, TargetKind, progress, cts.Token)).ConfigureAwait(true);
            if (id != _runId) return;
            try { await Task.Run(() => _snapshots.Save(snap)).ConfigureAwait(true); }
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
            ErrorText = Loc.Get(ex is AnalysisFailure f ? "Err." + (f.Kind == AnalysisFailureKind.Generic ? "Analysis" : f.Kind.ToString()) : "Err.Analysis");
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
