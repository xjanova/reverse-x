using ReverseX.Core.Rea;
using Xunit;

namespace ReverseX.Tests;

public class NodeProbeTests
{
    [Theory]
    [InlineData("22.19.0", true)]
    [InlineData("22.18.9", false)]
    [InlineData("22.20.1", true)]
    [InlineData("24.11.0", true)]
    [InlineData("24.10.9", false)]
    [InlineData("24.11.1", true)]
    [InlineData("23.5.0", false)]
    [InlineData("25.0.0", false)]
    [InlineData("26.0.0", true)]
    [InlineData("30.1.0", true)]
    [InlineData("20.11.0", false)]
    public void IsSupported_FollowsReaEnginesRange(string version, bool expected)
    {
        Assert.Equal(expected, NodeProbe.IsSupported(Version.Parse(version)));
    }

    [Theory]
    [InlineData("v24.11.1\n", 24, 11, 1)]
    [InlineData("22.19.0", 22, 19, 0)]
    public void ParseVersion_ReadsNodeOutput(string text, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), NodeProbe.ParseVersion(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not node")]
    [InlineData(null)]
    public void ParseVersion_ReturnsNullForGarbage(string? text)
    {
        Assert.Null(NodeProbe.ParseVersion(text));
    }

    [Fact]
    public void Launcher_PinsReaVersion()
    {
        var node = new NodeInfo(@"C:\nodejs\node.exe", new Version(24, 11, 1));
        var psi = ReaLauncher.Create(node, new[] { "mcp" });
        Assert.Contains("rea-agents@6.4.0", psi.ArgumentList);
        Assert.Equal("mcp", psi.ArgumentList[^1]);
    }
}
