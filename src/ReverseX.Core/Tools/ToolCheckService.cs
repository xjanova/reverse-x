using ReverseX.Core.Rea;

namespace ReverseX.Core.Tools;

/// <summary>Runs every probe (Node, REA doctor/providers, Java, JADX, browser) and returns the mapped rows. Never throws for tool problems.</summary>
public static class ToolCheckService
{
    public static async Task<IReadOnlyList<ToolStatus>> CheckAsync(CancellationToken ct = default)
    {
        var javaTask = SafeAsync(() => LocalToolProbe.JavaMajorAsync(ct));
        var localTask = Task.Run(() => (Jadx: LocalToolProbe.JadxFound(), Browser: LocalToolProbe.BrowserName()), ct);

        NodeInfo? node = null;
        try { node = await NodeProbe.FindAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppPaths.Log("Node probe failed", ex); }

        ReaDoctorReport? doctor = null;
        ReaCapabilityReport? providers = null;
        string? failure = null;
        if (node is not null && NodeProbe.IsSupported(node.Version))
        {
            try
            {
                var cli = new ReaCli(node, TimeSpan.FromMinutes(2));
                var d = cli.DoctorAsync(ct);
                var p = cli.ProvidersAsync(ct);
                doctor = await d.ConfigureAwait(false);
                providers = await p.ConfigureAwait(false);
            }
            catch (TimeoutException ex) { failure = "timeout"; AppPaths.Log("REA check timed out", ex); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = "failed";
                AppPaths.Log("REA check failed", ex);
            }
        }

        var java = await javaTask.ConfigureAwait(false);
        var local = await localTask.ConfigureAwait(false);
        return ToolStatusMapper.Map(new ToolProbeInputs(node, true, doctor, providers, failure, java, local.Jadx, local.Browser));
    }

    private static async Task<int?> SafeAsync(Func<Task<int?>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppPaths.Log("Java probe failed", ex); return null; }
    }
}
