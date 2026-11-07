using ReverseX.Core.Rea;

namespace ReverseX.Core.Analysis;

/// <summary>
/// Keeps one REA process alive for the file on screen. A deep engine pays a large one-time cost per target — Ghidra imports and
/// auto-analyzes the binary (a minute or more), JADX loads the APK — and REA keeps that work only for the life of its process,
/// so starting a fresh process per click would redo it every time.
/// <para>Calls are run one at a time: an engine session answers one request at a time anyway, and queuing here means a second
/// click waits for the analysis the first click started instead of cancelling it (REA drops Ghidra's database on cancel).</para>
/// </summary>
public sealed class TargetSessionCache : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IReaSession>> _start;
    private readonly Func<IReaToolCaller, string, CancellationToken, Task>? _prepare;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReaSession? _session;
    private string? _key;
    private bool _disposed;

    /// <param name="start">Creates a new REA session (the process itself may start lazily on its first call).</param>
    /// <param name="prepare">Runs once per (session, target) before any work — e.g. open_binary for native files.</param>
    public TargetSessionCache(Func<CancellationToken, Task<IReaSession>> start, Func<IReaToolCaller, string, CancellationToken, Task>? prepare = null)
    {
        _start = start;
        _prepare = prepare;
    }

    /// <summary>True while a process is kept for some target (for tests and diagnostics).</summary>
    public bool HasSession => _session is not null;

    /// <summary>
    /// Runs <paramref name="work"/> against the session that holds <paramref name="path"/>, creating (and preparing) it when the
    /// cached one belongs to another file, or to an older version of this file. A process that died, or a call that timed out,
    /// drops the session so the next call starts clean.
    /// </summary>
    public async Task<T> RunAsync<T>(string path, Func<IReaToolCaller, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = KeyOf(path);
            if (_session is null || _key != key)
            {
                await DropAsync().ConfigureAwait(false);
                var session = await _start(ct).ConfigureAwait(false);
                try
                {
                    if (_prepare is not null) await _prepare(session, path, ct).ConfigureAwait(false);
                }
                catch
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                _session = session;
                _key = key;
            }
            return await work(_session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBroken(ex))
        {
            await DropAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The process (not the question) is the problem — also when AnalysisRunner has already wrapped it for the UI.</summary>
    private static bool IsBroken(Exception ex) =>
        ex is ReaException or TimeoutException
        || ex is AnalysisFailure { Kind: AnalysisFailureKind.ReaUnavailable or AnalysisFailureKind.Timeout };

    /// <summary>Ends the kept process (a new target is on screen, or the app is closing).</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DropAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DropAsync()
    {
        var s = _session;
        _session = null;
        _key = null;
        if (s is not null)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppPaths.Log("Could not end a REA session", ex); }
        }
    }

    /// <summary>Path + size + write time: a rebuilt file at the same path must not be answered from the old analysis.</summary>
    public static string KeyOf(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                return full.ToUpperInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return full.ToUpperInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await DropAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
