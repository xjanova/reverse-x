using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReverseX.Core.Rea;

public sealed record NodeInfo(string Path, Version Version)
{
    public string NpxCliJs => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path)!, "node_modules", "npm", "bin", "npx-cli.js");
}

/// <summary>Finds Node.js and checks it against the range REA 6.4.0 declares: ^22.19.0 || ^24.11.0 || >=26.0.0.</summary>
public static class NodeProbe
{
    public const string RequirementText = "Node.js 22.19+ / 24.11+ / 26+";

    public static bool IsSupported(Version v) =>
        v.Major switch
        {
            22 => v.Minor >= 19,
            24 => v.Minor >= 11,
            >= 26 => true,
            _ => false,
        };

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"v?(\d+)\.(\d+)\.(\d+)");
        return m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;
    }

    /// <summary>Locates node on PATH (and the default install folder). Returns null when not installed.</summary>
    public static async Task<NodeInfo?> FindAsync(CancellationToken ct = default)
    {
        foreach (var candidate in Candidates())
        {
            var version = await TryGetVersionAsync(candidate, ct).ConfigureAwait(false);
            if (version is not null) return new NodeInfo(candidate, version);
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = System.IO.Path.Combine(dir.Trim('"'), exe); }
            catch (ArgumentException) { continue; }
            if (File.Exists(full) && seen.Add(full)) yield return full;
        }
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (pf is not null)
            {
                var full = System.IO.Path.Combine(pf, "nodejs", exe);
                if (File.Exists(full) && seen.Add(full)) yield return full;
            }
        }
    }

    private static async Task<Version?> TryGetVersionAsync(string nodePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(nodePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return ParseVersion(output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
