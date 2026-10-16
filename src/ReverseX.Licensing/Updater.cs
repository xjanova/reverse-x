using System.Diagnostics;
using System.Text.Json;

namespace ReverseX.Licensing;

/// <summary>
/// Puts a verified update in place. Runs as its own process from the NEW version's folder
/// ("ReverseX.exe apply-update &lt;new app folder&gt; &lt;install folder&gt; &lt;version&gt; &lt;old pid&gt; &lt;data folder&gt;"):
/// wait for the old app to exit → back up the install folder → copy the new files → start the new app.
/// If copying fails, the backup is restored and the old version is started again.
/// The outcome is left in update\last-result.json for the next start to show once.
/// </summary>
public static class Updater
{
    public const string Command = "apply-update";

    public sealed record Outcome(string Version, bool Ok, bool RolledBack, string Detail, DateTime At);

    public static string ResultFile(string dataDir) => Path.Combine(dataDir, "update", "last-result.json");

    /// <summary>The result the last update left behind, removed once read (null when there is none).</summary>
    public static Outcome? TakeOutcome(string dataDir)
    {
        var path = ResultFile(dataDir);
        try
        {
            if (!File.Exists(path)) return null;
            var outcome = JsonSerializer.Deserialize<Outcome>(File.ReadAllText(path));
            File.Delete(path);
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Entry for "apply-update"; returns the process exit code.</summary>
    public static int Run(string[] args, Action<ProcessStartInfo>? launch = null, TimeSpan? waitForExit = null)
    {
        if (args.Length < 6 || args[0] != Command) return 2;
        var (source, target, version, dataDir) = (args[1], args[2], args[3], args[5]);
        int.TryParse(args[4], out var pid);
        launch ??= psi => Process.Start(psi)?.Dispose();
        var log = new List<string>();
        void Log(string line) => log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        void Write(Outcome o)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(dataDir, "update"));
                File.WriteAllText(ResultFile(dataDir), JsonSerializer.Serialize(o));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("outcome not written: " + ex.Message);
            }
        }

        var backup = Path.Combine(dataDir, "update", "backup");
        var ok = false;
        try
        {
            Log($"waiting for pid {pid}");
            if (!WaitForExit(pid, waitForExit ?? TimeSpan.FromSeconds(60))) throw new IOException("Reverse X ยังไม่ปิดภายใน 60 วินาที");

            // only the files the update replaces are backed up: the folder may hold other things (a portable copy on the Desktop)
            Log("backing up " + target);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            var added = BackupReplaced(source, target, backup);

            try
            {
                Log("copying " + source);
                CopyTree(source, target);
                ok = true;
                Write(new Outcome(version, true, false, "", DateTime.UtcNow));
                try { Directory.Delete(backup, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ลบรอบหน้า */ }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("rolling back: " + ex.Message);
                string detail;
                try
                {
                    CopyTree(backup, target);
                    foreach (var file in added)
                        if (File.Exists(file)) File.Delete(file);
                    detail = ex.Message;
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    detail = $"{ex.Message} · restore: {restore.Message} · backup: {backup}";
                }
                Write(new Outcome(version, false, true, detail, DateTime.UtcNow));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // แอปเดิมยังไม่ปิด / สำรองไม่ได้: ไม่แตะไฟล์โปรแกรม
            Write(new Outcome(version, false, false, ex.Message, DateTime.UtcNow));
        }

        Log(ok ? "done" : "failed");
        try { File.AppendAllLines(Path.Combine(dataDir, "update", "update.log"), log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ไม่มีที่เขียน log */ }

        // เปิดโปรแกรมในโฟลเดอร์ติดตั้งเสมอ (รุ่นใหม่ถ้าสำเร็จ รุ่นเดิมถ้าคืนไฟล์แล้ว)
        var exe = Path.Combine(target, UpdatePackageVerifier.MainExe);
        if (File.Exists(exe))
        {
            try { launch(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = target }); }
            catch (System.ComponentModel.Win32Exception) { /* ผู้ใช้เปิดเองได้ */ }
        }
        return ok ? 0 : 1;
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // ปิดไปแล้ว
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Copies the target's copy of every file the package carries into <paramref name="backup"/>; returns the files that are new.</summary>
    internal static List<string> BackupReplaced(string source, string target, string backup)
    {
        var added = new List<string>();
        Directory.CreateDirectory(backup);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var existing = Path.Combine(target, relative);
            if (!File.Exists(existing))
            {
                added.Add(existing);
                continue;
            }
            var dest = Path.Combine(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(existing, dest, overwrite: true);
        }
        return added;
    }

    /// <summary>Copies every file of <paramref name="from"/> over <paramref name="to"/>, retrying files that are briefly locked.</summary>
    internal static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(file, dest, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(1000);
                }
            }
        }
    }
}
