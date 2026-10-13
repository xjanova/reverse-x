using System.Text.Json.Nodes;
using ReverseX.Core.Rea;

namespace ReverseX.Core.Analysis;

/// <summary>One REA MCP process for one analysis: starts on first use, keeps retained evidence alive between calls, dies on dispose.</summary>
public sealed class ReaSession : IReaToolCaller, IAsyncDisposable
{
    private readonly NodeInfo _node;
    private ReaClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReaSession(NodeInfo node) => _node = node;

    public async Task<ReaToolResult> CallAsync(string tool, JsonNode args, TimeSpan timeout, CancellationToken ct)
    {
        var client = await EnsureAsync(ct).ConfigureAwait(false);
        return await client.CallToolAsync(tool, args, timeout, ct).ConfigureAwait(false);
    }

    private async Task<ReaClient> EnsureAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _client ??= await ReaClient.StartAsync(new ReaClientOptions
            {
                Node = _node,
                RequestTimeout = TimeSpan.FromMinutes(5),
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>CLI route for results too big for the MCP budget; returns stdout JSON.</summary>
    public static Func<string, CancellationToken, Task<string>> ManagedMembersViaCli(NodeInfo node) => async (path, ct) =>
    {
        var cli = new ReaCli(node, TimeSpan.FromMinutes(10));
        var o = await cli.RunAsync(new[] { "inspect-managed-members", path, "--format", "json", "--full-output" }, ct).ConfigureAwait(false);
        return o.StdOut;
    };

    public async ValueTask DisposeAsync()
    {
        var c = _client;
        _client = null;
        if (c is not null) await c.DisposeAsync().ConfigureAwait(false);
    }
}
