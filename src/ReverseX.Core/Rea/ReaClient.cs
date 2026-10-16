using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReverseX.Core.Rea;

public sealed class ReaException : Exception
{
    public int? Code { get; }
    public ReaException(string message, int? code = null) : base(message) => Code = code;
}

public sealed record ReaTool(string Name, string? Description, JsonElement InputSchema);

public sealed record ReaToolResult(bool IsError, string Text, JsonElement Raw);

public sealed record ReaServerInfo(string Name, string Version, string ProtocolVersion);

public sealed class ReaClientOptions
{
    public NodeInfo Node { get; init; } = null!;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public string? WorkingDirectory { get; init; }
    public string ProtocolVersion { get; init; } = "2025-06-18";
}

/// <summary>
/// Minimal MCP client over stdio (newline-delimited JSON-RPC 2.0) that talks to
/// "npx rea-agents@6.4.0 mcp".
/// </summary>
public sealed class ReaClient : IAsyncDisposable
{
    private readonly ReaClientOptions _options;
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _readerTask;
    private readonly Task _stderrTask;
    private long _nextId;
    private int _disposed;
    private Exception? _readerFailure;

    public ReaServerInfo? ServerInfo { get; private set; }
    public string StandardError { get { lock (_stderr) return _stderr.ToString(); } }

    private ReaClient(ReaClientOptions options, Process process)
    {
        _options = options;
        _process = process;
        _readerTask = Task.Run(ReadLoopAsync);
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (_stderr)
                    {
                        if (_stderr.Length < 64 * 1024) _stderr.AppendLine(line);
                    }
                }
            }
            catch (Exception) { /* stream closed */ }
        });
    }

    /// <summary>Starts the REA MCP server and performs the MCP initialize handshake.</summary>
    public static async Task<ReaClient> StartAsync(ReaClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options.Node);
        var psi = ReaLauncher.Create(options.Node, new[] { "mcp" }, options.WorkingDirectory);
        var process = Process.Start(psi) ?? throw new ReaException("Could not start the REA process.");
        var client = new ReaClient(options, process);
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = _options.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "ReverseX", ["version"] = "0.1.0" },
        }, _options.StartupTimeout, ct).ConfigureAwait(false);

        var info = result.TryGetProperty("serverInfo", out var si) ? si : default;
        ServerInfo = new ReaServerInfo(
            info.ValueKind == JsonValueKind.Object && info.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            info.ValueKind == JsonValueKind.Object && info.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
            result.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() ?? "" : "");

        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReaTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<ReaTool>();
        string? cursor = null;
        do
        {
            var p = new JsonObject();
            if (cursor is not null) p["cursor"] = cursor;
            var result = await RequestAsync("tools/list", p, _options.RequestTimeout, ct).ConfigureAwait(false);
            if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    tools.Add(new ReaTool(
                        t.GetProperty("name").GetString() ?? "",
                        t.TryGetProperty("description", out var d) ? d.GetString() : null,
                        t.TryGetProperty("inputSchema", out var s) ? s.Clone() : default));
                }
            }
            cursor = result.TryGetProperty("nextCursor", out var nc) ? nc.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }

    public async Task<ReaToolResult> CallToolAsync(
        string name, JsonNode? arguments = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments ?? new JsonObject(),
        }, timeout ?? _options.RequestTimeout, ct).ConfigureAwait(false);

        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && item.TryGetProperty("text", out var tx))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(tx.GetString());
                }
            }
        }
        return new ReaToolResult(isError, text.ToString(), result.Clone());
    }

    private async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // The timeout clock starts before the request is written, so it can expire while
            // SendAsync waits for the write lock or the pipe. Both phases must report a timeout
            // as TimeoutException; only the caller's own token means OperationCanceledException.
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters,
                }, cts.Token).ConfigureAwait(false);

                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"REA did not answer '{method}' within {timeout.TotalSeconds:0}s.");
            }
            catch (OperationCanceledException)
            {
                // Tell the server we no longer care (best effort).
                _ = SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/cancelled",
                    ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "client cancelled" },
                }, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
                throw;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(JsonNode message, CancellationToken ct)
    {
        var line = message.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process.HasExited) throw new ReaException("The REA process has exited. " + StandardError.Trim());
            var stream = _process.StandardInput;
            await stream.WriteAsync((line + "\n").AsMemory(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new ReaException("Could not write to the REA process: " + ex.Message + " " + StandardError.Trim());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0 || line[0] != '{') continue; // ignore stray non-JSON output
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    await HandleMessageAsync(doc.RootElement).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _readerFailure = ex;
        }
        finally
        {
            var err = new ReaException("The REA process closed its output. " + StandardError.Trim());
            foreach (var kv in _pending) kv.Value.TrySetException(_readerFailure ?? err);
        }
    }

    private async Task HandleMessageAsync(JsonElement msg)
    {
        var hasId = msg.TryGetProperty("id", out var idEl);
        var hasMethod = msg.TryGetProperty("method", out _);

        if (hasId && !hasMethod)
        {
            if (idEl.ValueKind == JsonValueKind.Number && _pending.TryGetValue(idEl.GetInt64(), out var tcs))
            {
                if (msg.TryGetProperty("error", out var err))
                {
                    var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : (int?)null;
                    var text = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                    tcs.TrySetException(new ReaException(text ?? "unknown error", code));
                }
                else if (msg.TryGetProperty("result", out var result))
                {
                    tcs.TrySetResult(result.Clone());
                }
            }
        }
        else if (hasId && hasMethod)
        {
            // Server → client request we do not support (roots, sampling…): answer so it never hangs.
            msg.TryGetProperty("method", out var method);
            var isPing = method.GetString() == "ping";
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(idEl.GetRawText()) };
            if (isPing) reply["result"] = new JsonObject();
            else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            try { await SendAsync(reply, CancellationToken.None).ConfigureAwait(false); }
            catch (ReaException) { }
        }
        // Notifications (logging, progress) are ignored in phase 1.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try { _process.StandardInput.Close(); } catch (Exception) { }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
                try { await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            // If npx left the server running after the wrapper exited, make sure the tree is gone.
            try { await Task.WhenAll(_readerTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        finally
        {
            _process.Dispose();
            _writeLock.Dispose();
        }
    }
}
