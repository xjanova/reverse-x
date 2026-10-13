using ReverseX.Core.Rea;

namespace ReverseX.Core.Tools;

/// <summary>Turns raw probe results (REA doctor/providers, Node, Java…) into green / yellow / red / grey rows.</summary>
public static class ToolStatusMapper
{
    public static IReadOnlyList<ToolStatus> Map(ToolProbeInputs i) => new[]
    {
        MapNode(i),
        MapRea(i),
        MapGhidra(i),
        MapJadx(i),
        MapBrowser(i),
        MapIda(i),
    };

    public static IReadOnlyList<ToolStatus> Checking() => new[]
    {
        new ToolStatus("node", "Node.js", ToolLevel.Checking, "checking"),
        new ToolStatus("rea", "REA", ToolLevel.Checking, "checking"),
        new ToolStatus("ghidra", "Ghidra", ToolLevel.Checking, "checking"),
        new ToolStatus("jadx", "JADX", ToolLevel.Checking, "checking"),
        new ToolStatus("browser", "Chrome / Edge", ToolLevel.Checking, "checking"),
        new ToolStatus("ida", "IDA Pro · Hopper", ToolLevel.Checking, "checking"),
    };

    private static ToolStatus MapNode(ToolProbeInputs i)
    {
        if (i.Node is null) return new("node", "Node.js", ToolLevel.Bad, "node.missing");
        var v = i.Node.Version.ToString();
        return NodeProbe.IsSupported(i.Node.Version)
            ? new("node", $"Node.js {v}", ToolLevel.Ok, "node.ok")
            : new("node", $"Node.js {v}", ToolLevel.Warn, "node.old", v);
    }

    private static ToolStatus MapRea(ToolProbeInputs i)
    {
        if (i.Node is null || !NodeProbe.IsSupported(i.Node.Version))
            return new("rea", "REA", ToolLevel.Off, "rea.needNode");
        if (i.ReaFailure == "timeout") return new("rea", "REA", ToolLevel.Bad, "rea.timeout");
        if (i.Doctor is null) return new("rea", "REA", ToolLevel.Bad, "rea.failed");
        var ver = i.Doctor.CliPackageVersion ?? ReaLauncher.PinnedVersion;
        var tools = i.Doctor.McpToolCount?.ToString() ?? "-";
        return new("rea", $"REA {ver}", ToolLevel.Ok, "rea.ok", ver, tools);
    }

    private static ToolStatus MapGhidra(ToolProbeInputs i)
    {
        var ghidra = Candidate(i, "ghidra");
        if (ghidra is { Status: var s } && IsAvailable(s)) return new("ghidra", "Ghidra", ToolLevel.Ok, "ghidra.ok");
        if (i.JavaMajor is null) return new("ghidra", "Ghidra", ToolLevel.Warn, "ghidra.noJava");
        if (i.JavaMajor < 21) return new("ghidra", "Ghidra", ToolLevel.Warn, "ghidra.needJdk21", i.JavaMajor.Value.ToString());
        return i.Providers is null
            ? new("ghidra", "Ghidra", ToolLevel.Off, "ghidra.unknown")
            : new("ghidra", "Ghidra", ToolLevel.Warn, "ghidra.notSet");
    }

    private static ToolStatus MapJadx(ToolProbeInputs i) =>
        i.JadxFound
            ? new("jadx", "JADX", ToolLevel.Ok, "jadx.ok")
            : new("jadx", "JADX", ToolLevel.Bad, "jadx.missing");

    private static ToolStatus MapBrowser(ToolProbeInputs i) =>
        i.BrowserName is null
            ? new("browser", "Chrome / Edge", ToolLevel.Warn, "browser.missing")
            : new("browser", i.BrowserName, ToolLevel.Ok, "browser.ok");

    private static ToolStatus MapIda(ToolProbeInputs i)
    {
        var ida = Candidate(i, "ida");
        return ida is { Status: var s } && IsAvailable(s)
            ? new("ida", "IDA Pro", ToolLevel.Ok, "ida.ok")
            : new("ida", "IDA Pro · Hopper", ToolLevel.Off, "ida.optional");
    }

    private static ReaProviderCandidate? Candidate(ToolProbeInputs i, string id) =>
        i.Providers?.ProviderCandidates.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsAvailable(string? status) =>
        status is not null && (status.Equals("available", StringComparison.OrdinalIgnoreCase)
                               || status.Equals("ready", StringComparison.OrdinalIgnoreCase));
}
