using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.Core.Ai;

/// <summary>One REA process kept alive for the chat about one project: the selected tools plus the caller that runs them.</summary>
public sealed class ReaToolSession : IAsyncDisposable
{
    private readonly ReaSession _session;
    public IReaToolCaller Caller => _session;
    public IReadOnlyList<ReaTool> Tools { get; }
    public string TargetPath { get; }

    private ReaToolSession(ReaSession session, IReadOnlyList<ReaTool> tools, string target)
    {
        _session = session;
        Tools = tools;
        TargetPath = target;
    }

    /// <summary>Starts REA and keeps only the tools that make sense for this kind of project. Throws when Node.js/REA is unavailable.</summary>
    public static async Task<ReaToolSession> StartAsync(string target, FileKind kind, CancellationToken ct)
    {
        var node = await NodeProbe.FindAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("node not found");
        var session = new ReaSession(node);
        try
        {
            var all = await session.ListToolsAsync(ct).ConfigureAwait(false);
            return new ReaToolSession(session, ToolSelector.Select(kind, all), target);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
