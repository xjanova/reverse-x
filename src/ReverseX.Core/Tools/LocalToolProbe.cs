using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReverseX.Core.Tools;

/// <summary>Probes for tools REA does not report itself: Java, JADX, a Chromium browser.</summary>
public static class LocalToolProbe
{
    /// <summary>"1.8.0_392" → 8, "17.0.18" → 17, "21" → 21.</summary>
    public static int? ParseJavaMajor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
        if (!m.Success) return null;
        var major = int.Parse(m.Groups[1].Value);
        return major == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : major;
    }

    public static async Task<int?> JavaMajorAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));
        candidates.Add("java");
        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "-version")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var err = await p.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var v = ParseJavaMajor(err);
                if (v is not null) return v;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
            }
        }
        return null;
    }

    public static bool JadxFound()
    {
        var jadxHome = Environment.GetEnvironmentVariable("JADX_HOME");
        if (!string.IsNullOrWhiteSpace(jadxHome) && ExistsAny(Path.Combine(jadxHome, "bin"), "jadx.bat", "jadx")) return true;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (ExistsAny(dir.Trim('"'), "jadx.bat", "jadx.cmd", "jadx.exe", "jadx", "jadx-gui.bat")) return true;
        return false;
    }

    public static string? BrowserName()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var local = Environment.GetEnvironmentVariable("LocalAppData");
        string?[] chrome =
        {
            pf is null ? null : Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            pf86 is null ? null : Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            local is null ? null : Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
        };
        if (chrome.Any(p => p is not null && File.Exists(p))) return "Chrome";
        string?[] edge =
        {
            pf86 is null ? null : Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            pf is null ? null : Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return edge.Any(p => p is not null && File.Exists(p)) ? "Edge" : null;
    }

    private static bool ExistsAny(string dir, params string[] names)
    {
        try { return Directory.Exists(dir) && names.Any(n => File.Exists(Path.Combine(dir, n))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
}
