using System.Diagnostics;
using System.Text.Json;

namespace ReverseX.Core.Rea;

public sealed record ReaDoctorCheck(string Name, bool Ok, string? Classification, string? Detail, string? Remediation);

public sealed record ReaDoctorReport(
    bool Healthy,
    bool EnvironmentHealthy,
    string? CliPackageVersion,
    int? McpToolCount,
    int? CliCommandCount,
    IReadOnlyList<ReaDoctorCheck> Checks,
    JsonElement Raw);

public sealed record ReaCapability(string Operation, bool Available, string? Reason, string? AvailabilityCode);

public sealed record ReaProviderCandidate(string Id, string? Name, string? Status, string? Reason);

public sealed record ReaCapabilityReport(
    IReadOnlyList<ReaCapability> Capabilities,
    IReadOnlyList<ReaProviderCandidate> ProviderCandidates,
    JsonElement Raw);

public sealed record ReaCliOutput(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs "rea doctor|providers|capabilities --json" and parses the result.</summary>
public sealed class ReaCli
{
    private readonly NodeInfo _node;
    private readonly TimeSpan _timeout;

    public ReaCli(NodeInfo node, TimeSpan? timeout = null)
    {
        _node = node;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public async Task<ReaDoctorReport> DoctorAsync(CancellationToken ct = default) =>
        ParseDoctor(await RunJsonAsync("doctor", ct).ConfigureAwait(false));

    public async Task<ReaCapabilityReport> ProvidersAsync(CancellationToken ct = default) =>
        ParseCapabilities(await RunJsonAsync("providers", ct).ConfigureAwait(false));

    public async Task<ReaCapabilityReport> CapabilitiesAsync(CancellationToken ct = default) =>
        ParseCapabilities(await RunJsonAsync("capabilities", ct).ConfigureAwait(false));

    private async Task<string> RunJsonAsync(string command, CancellationToken ct)
    {
        // "doctor" exits 1 when the environment is unhealthy but still prints valid JSON,
        // so the exit code alone does not mean failure.
        var output = await RunAsync(new[] { command, "--json" }, ct).ConfigureAwait(false);
        var text = output.StdOut.Trim();
        if (text.Length == 0 || text[0] != '{')
            throw new ReaException($"'rea {command} --json' returned no JSON (exit {output.ExitCode}): {output.StdErr.Trim()}");
        return text;
    }

    public async Task<ReaCliOutput> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = ReaLauncher.Create(_node, args);
        using var process = Process.Start(psi) ?? throw new ReaException("Could not start the REA process.");
        process.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ReaCliOutput(process.ExitCode,
                await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"REA CLI did not finish within {_timeout.TotalSeconds:0}s.");
        }
    }

    public static ReaDoctorReport ParseDoctor(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var checks = new List<ReaDoctorCheck>();
        if (root.TryGetProperty("checks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
                checks.Add(new ReaDoctorCheck(Str(c, "name") ?? "", Bool(c, "ok"), Str(c, "classification"), Str(c, "detail"), Str(c, "remediation")));
        }
        string? pkg = null; int? tools = null, cmds = null;
        if (root.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            pkg = Str(id, "cli_package_version");
            if (id.TryGetProperty("catalog", out var cat) && cat.TryGetProperty("counts", out var counts))
            {
                tools = Int(counts, "mcp_tools");
                cmds = Int(counts, "cli_commands");
            }
        }
        return new ReaDoctorReport(Bool(root, "healthy"), Bool(root, "environment_healthy"), pkg, tools, cmds, checks, root.Clone());
    }

    public static ReaCapabilityReport ParseCapabilities(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var caps = new List<ReaCapability>();
        if (root.TryGetProperty("capabilities", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
                caps.Add(new ReaCapability(Str(c, "operation") ?? "", Bool(c, "available"), Str(c, "reason"), Str(c, "availability_code")));
        }
        var cands = new List<ReaProviderCandidate>();
        if (root.TryGetProperty("analysis_provider_candidates", out var pc) && pc.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in pc.EnumerateArray())
            {
                var prov = c.TryGetProperty("provider", out var p) ? p : default;
                var av = c.TryGetProperty("availability", out var a) ? a : default;
                cands.Add(new ReaProviderCandidate(
                    prov.ValueKind == JsonValueKind.Object ? Str(prov, "id") ?? "" : "",
                    prov.ValueKind == JsonValueKind.Object ? Str(prov, "name") : null,
                    av.ValueKind == JsonValueKind.Object ? Str(av, "status") : null,
                    av.ValueKind == JsonValueKind.Object ? Str(av, "reason") : null));
            }
        }
        return new ReaCapabilityReport(caps, cands, root.Clone());
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
}
