using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

public class ReaCliParseTests
{
    private const string DoctorJson = """
    {"healthy":false,"environment_healthy":false,
     "checks":[
       {"name":"node","ok":true,"classification":"healthy","detail":"24.11.1"},
       {"name":"ghidra","ok":false,"classification":"missing_analysis_engine","detail":"GHIDRA_INSTALL_DIR is not set","remediation":"Set it."}],
     "identity":{"cli_package_version":"6.4.0","catalog":{"counts":{"cli_commands":127,"mcp_tools":170,"mcp_prompts":6}}}}
    """;

    private const string ProvidersJson = """
    {"open":false,
     "capabilities":[
       {"operation":"extract_artifact","available":true,"reason":null,"availability_code":null},
       {"operation":"demangle_swift","available":false,"reason":"Native macOS utilities require macOS.","availability_code":"unsupported_host"}],
     "analysis_provider_candidates":[
       {"provider":{"id":"ghidra","name":"Ghidra","version":null},"availability":{"status":"unavailable","code":"not_configured","reason":"Set GHIDRA_INSTALL_DIR"}}]}
    """;

    [Fact]
    public void ParseDoctor_ReadsChecksAndIdentity()
    {
        var r = ReaCli.ParseDoctor(DoctorJson);
        Assert.False(r.Healthy);
        Assert.Equal("6.4.0", r.CliPackageVersion);
        Assert.Equal(170, r.McpToolCount);
        Assert.Equal(127, r.CliCommandCount);
        Assert.Equal(2, r.Checks.Count);
        Assert.True(r.Checks[0].Ok);
        Assert.Equal("missing_analysis_engine", r.Checks[1].Classification);
        Assert.Equal("Set it.", r.Checks[1].Remediation);
    }

    [Fact]
    public void ParseCapabilities_ReadsOperationsAndProviders()
    {
        var r = ReaCli.ParseCapabilities(ProvidersJson);
        Assert.Equal(2, r.Capabilities.Count);
        Assert.True(r.Capabilities[0].Available);
        Assert.Equal("unsupported_host", r.Capabilities[1].AvailabilityCode);
        var p = Assert.Single(r.ProviderCandidates);
        Assert.Equal("ghidra", p.Id);
        Assert.Equal("unavailable", p.Status);
    }

    [Fact]
    public void ParseDoctor_ToleratesMissingFields()
    {
        var r = ReaCli.ParseDoctor("{}");
        Assert.False(r.Healthy);
        Assert.Empty(r.Checks);
        Assert.Null(r.CliPackageVersion);
    }
}
