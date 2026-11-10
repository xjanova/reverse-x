using System.Diagnostics;
using ReverseX.Core.Engines;

namespace ReverseX.Core.Rea;

/// <summary>Builds the process start info that runs the pinned REA package through npx.</summary>
public static class ReaLauncher
{
    public const string PackageName = "rea-agents";
    public const string PinnedVersion = "6.4.0";
    public static string PackageSpec => $"{PackageName}@{PinnedVersion}";

    /// <param name="engines">
    /// Engine selection passed to REA (GHIDRA_INSTALL_DIR, JAVA_HOME, PATH, REA_JADX_MCP_JAR); null resolves it now with
    /// <see cref="EngineLocator.ResolveDefault"/>, so engines installed while the app runs are used by the next REA start.
    /// </param>
    public static ProcessStartInfo Create(NodeInfo node, IEnumerable<string> reaArgs, string? workingDirectory = null,
        EngineEnvironment? engines = null)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

        // Prefer "node npx-cli.js" (no cmd.exe / .cmd shim, no quoting problems).
        if (File.Exists(node.NpxCliJs))
        {
            psi.FileName = node.Path;
            psi.ArgumentList.Add(node.NpxCliJs);
        }
        else if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("npx");
        }
        else
        {
            psi.FileName = "npx";
        }
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(PackageSpec);
        foreach (var a in reaArgs) psi.ArgumentList.Add(a);
        (engines ?? EngineLocator.ResolveDefault()).ApplyTo(psi.Environment);
        psi.Environment["NO_COLOR"] = "1";
        return psi;
    }
}
