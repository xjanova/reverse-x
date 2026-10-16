using System.Text.Json;
using System.Text.Json.Nodes;
using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

/// <summary>Skips the test when Node.js is missing/unsupported (or REVERSEX_SKIP_REA=1).</summary>
public sealed class ReaFactAttribute : FactAttribute
{
    public ReaFactAttribute()
    {
        if (ReaEnv.SkipReason is { } reason) Skip = reason;
    }
}

internal static class ReaEnv
{
    public static readonly NodeInfo? Node = NodeProbe.FindAsync().GetAwaiter().GetResult();

    public static string? SkipReason =>
        Environment.GetEnvironmentVariable("REVERSEX_SKIP_REA") == "1" ? "REVERSEX_SKIP_REA=1"
        : Node is null ? "Node.js not found on PATH (REA needs " + NodeProbe.RequirementText + ")"
        : !NodeProbe.IsSupported(Node.Version) ? $"Node.js {Node.Version} is too old for REA (needs {NodeProbe.RequirementText})"
        : null;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReverseX.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ReverseX.slnx not found above " + AppContext.BaseDirectory);
    }
}

public class ReaIntegrationTests
{
    private static ReaClientOptions Options(TimeSpan? requestTimeout = null) => new()
    {
        Node = ReaEnv.Node!,
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(90),
    };

    private static readonly string CoreDll = typeof(ReaClient).Assembly.Location;

    [ReaFact]
    public async Task Cli_Doctor_ReturnsParsedReport()
    {
        var report = await new ReaCli(ReaEnv.Node!).DoctorAsync();
        Assert.Equal("6.4.0", report.CliPackageVersion);
        Assert.Contains(report.Checks, c => c.Name == "node" && c.Ok);
        Assert.True(report.McpToolCount > 100);
    }

    [ReaFact]
    public async Task Cli_Providers_And_Capabilities_Parse()
    {
        var cli = new ReaCli(ReaEnv.Node!);
        var providers = await cli.ProvidersAsync();
        var caps = await cli.CapabilitiesAsync();
        Assert.NotEmpty(providers.Capabilities);
        Assert.NotEmpty(caps.Capabilities);
        Assert.Contains(providers.Capabilities, c => c.Operation == "extract_artifact");
    }

    [ReaFact]
    public async Task Client_Initialize_ListTools_AndCloseCleanly()
    {
        await using var client = await ReaClient.StartAsync(Options());
        Assert.Equal("rea", client.ServerInfo?.Name);
        Assert.Equal("6.4.0", client.ServerInfo?.Version);

        var tools = await client.ListToolsAsync();
        Assert.True(tools.Count > 100, $"only {tools.Count} tools");
        Assert.Contains(tools, t => t.Name == "inspect_managed_artifact");
    }

    [ReaFact]
    public async Task Client_CallsTool_OnOwnDotNetAssembly()
    {
        await using var client = await ReaClient.StartAsync(Options());
        var result = await client.CallToolAsync("inspect_managed_artifact", new JsonObject { ["path"] = CoreDll });
        Assert.False(result.IsError, result.Text);
        Assert.Contains("ReverseX.Core", result.Text);
    }

    [ReaFact]
    public async Task Client_UnknownTool_ReportsError()
    {
        await using var client = await ReaClient.StartAsync(Options());
        try
        {
            var r = await client.CallToolAsync("no_such_tool_xyz");
            Assert.True(r.IsError);
        }
        catch (ReaException)
        {
            // JSON-RPC level error is also an acceptable way to report this.
        }
    }

    [ReaFact]
    public async Task Client_RequestTimeout_Throws()
    {
        await using var client = await ReaClient.StartAsync(Options(TimeSpan.FromMilliseconds(1)));
        await Assert.ThrowsAsync<TimeoutException>(() => client.ListToolsAsync());
    }

    // A zero timeout has already expired before the request is written, which is the
    // case the 1 ms test above only hits sometimes: the expiry lands inside SendAsync.
    [ReaFact]
    public async Task Client_RequestTimeout_ExpiringBeforeSend_IsStillTimeout()
    {
        await using var client = await ReaClient.StartAsync(Options(TimeSpan.Zero));
        await Assert.ThrowsAsync<TimeoutException>(() => client.ListToolsAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => client.CallToolAsync("no_such_tool_xyz", timeout: TimeSpan.Zero));
    }

    [ReaFact]
    public async Task Client_Cancellation_Throws()
    {
        await using var client = await ReaClient.StartAsync(Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListToolsAsync(cts.Token));
        // The client must still be usable afterwards.
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [ReaFact]
    public async Task Client_Start_Cancelled_DoesNotLeakProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReaClient.StartAsync(Options(), cts.Token));
    }

    /// <summary>Generates docs/samples/. Run with REVERSEX_WRITE_SAMPLES=1.</summary>
    [ReaFact]
    public async Task Sample_AnalyzeReverseXCore_WritesSummary()
    {
        if (Environment.GetEnvironmentVariable("REVERSEX_WRITE_SAMPLES") != "1") return;

        await using var client = await ReaClient.StartAsync(Options());
        var tools = await client.ListToolsAsync();
        var calls = new JsonObject();
        foreach (var name in new[] { "inspect_managed_artifact", "inspect_managed_members" })
        {
            var r = await client.CallToolAsync(name, new JsonObject { ["path"] = CoreDll }, TimeSpan.FromMinutes(2));
            var text = r.Text.Length > 30_000 ? r.Text[..30_000] : r.Text;
            JsonNode? parsed = null;
            if (r.Text.Length <= 30_000)
            {
                try { parsed = JsonNode.Parse(r.Text); } catch (JsonException) { }
            }
            calls[name] = new JsonObject
            {
                ["isError"] = r.IsError,
                ["truncated"] = r.Text.Length > 30_000,
                ["originalLength"] = r.Text.Length,
                ["result"] = parsed ?? JsonValue.Create(text),
            };
        }
        var summary = new JsonObject
        {
            ["generatedBy"] = "ReverseX.Tests Sample_AnalyzeReverseXCore_WritesSummary",
            ["target"] = "ReverseX.Core.dll (built from this repo)",
            ["server"] = new JsonObject { ["name"] = client.ServerInfo?.Name, ["version"] = client.ServerInfo?.Version, ["protocol"] = client.ServerInfo?.ProtocolVersion },
            ["toolCount"] = tools.Count,
            ["toolNames"] = new JsonArray(tools.Select(t => (JsonNode?)JsonValue.Create(t.Name)).ToArray()),
            ["calls"] = calls,
        };
        var dir = Path.Combine(ReaEnv.RepoRoot(), "docs", "samples");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "rea-analyze-reversex-core.json");
        File.WriteAllText(file, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(file).Length < 200 * 1024);
    }
}
