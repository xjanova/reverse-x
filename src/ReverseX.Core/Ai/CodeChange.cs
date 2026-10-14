using System.Text;

namespace ReverseX.Core.Ai;

/// <summary>Everything about the user's own project folder: sandboxed paths, diffs, and (only on confirmation) writing.</summary>
public static class CodeChange
{
    public const int MaxFileBytes = 200_000;
    private static readonly string[] Skipped = { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "build" };

    /// <summary>Full path of a relative file inside root, or null when it escapes the folder or touches .git.</summary>
    public static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        if (relative.Contains(':') || relative.Contains('\0')) return null; // drive letters, NTFS alternate streams
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootFull, relative)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = full[rootFull.Length..].Replace('\\', '/');
        if (rel.Split('/').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase))) return null;
        return full;
    }

    public static IEnumerable<string> ListFiles(string root, int max = 300)
    {
        var rootFull = Path.GetFullPath(root);
        var stack = new Stack<string>();
        stack.Push(rootFull);
        var n = 0;
        while (stack.Count > 0 && n < max)
        {
            var dir = stack.Pop();
            IEnumerable<string> subs, files;
            try { subs = Directory.EnumerateDirectories(dir); files = Directory.EnumerateFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (n++ >= max) yield break;
                yield return Path.GetRelativePath(rootFull, f).Replace('\\', '/');
            }
            foreach (var s in subs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                if (!Skipped.Contains(Path.GetFileName(s), StringComparer.OrdinalIgnoreCase)) stack.Push(s);
        }
    }

    public static FileProposal Propose(string root, string relative, string newContent, string explanation, out string? error)
    {
        error = null;
        var full = Resolve(root, relative);
        if (full is null) { error = "พาธต้องเป็นพาธสัมพัทธ์ภายในโฟลเดอร์โปรเจกต์ (ห้ามออกนอกโฟลเดอร์ / .git)"; return null!; }
        if (Encoding.UTF8.GetByteCount(newContent) > MaxFileBytes) { error = $"ไฟล์ใหญ่เกิน {MaxFileBytes / 1000} KB"; return null!; }
        string? old = null;
        if (File.Exists(full))
        {
            if (new FileInfo(full).Length > MaxFileBytes) { error = "ไฟล์เดิมใหญ่เกินไป"; return null!; }
            old = File.ReadAllText(full);
        }
        var rel = relative.Replace('\\', '/');
        return new FileProposal(rel, old, newContent, UnifiedDiff.Make(rel, old, newContent), explanation);
    }

    /// <summary>Writes the proposal. Called only after the user pressed confirm. Refuses if the file changed since the proposal was made.</summary>
    public static void Apply(string root, FileProposal p)
    {
        var full = Resolve(root, p.RelativePath) ?? throw new InvalidOperationException("พาธไม่ถูกต้อง");
        var current = File.Exists(full) ? File.ReadAllText(full) : null;
        if (current != p.OldContent) throw new InvalidOperationException("ไฟล์ถูกแก้ไขหลังจากที่ AI เสนอ — ขอให้ AI เสนอใหม่");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, p.NewContent, new UTF8Encoding(false));
    }
}

/// <summary>Small line diff (LCS) in unified-diff shape with 3 lines of context. Good enough for files up to the size cap.</summary>
public static class UnifiedDiff
{
    public static string Make(string path, string? oldText, string newText)
    {
        var a = Split(oldText ?? "");
        var b = Split(newText);
        var sb = new StringBuilder();
        sb.Append("--- ").AppendLine(oldText is null ? "/dev/null" : "a/" + path);
        sb.Append("+++ b/").AppendLine(path);

        var ops = Ops(a, b);
        const int ctx = 3;
        var i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Kind == ' ') i++;
            if (i >= ops.Count) break;
            var start = Math.Max(0, i - ctx);
            var end = i;
            // grow the hunk while another change lies within 2*ctx lines
            var lastChange = i;
            for (var j = i; j < ops.Count; j++)
            {
                if (ops[j].Kind != ' ') lastChange = j;
                else if (j - lastChange > ctx * 2) break;
            }
            end = Math.Min(ops.Count, lastChange + ctx + 1);
            int oldStart = 1, newStart = 1;
            for (var k = 0; k < start; k++) { if (ops[k].Kind != '+') oldStart++; if (ops[k].Kind != '-') newStart++; }
            var oldLen = 0; var newLen = 0;
            for (var k = start; k < end; k++) { if (ops[k].Kind != '+') oldLen++; if (ops[k].Kind != '-') newLen++; }
            sb.AppendLine($"@@ -{oldStart},{oldLen} +{newStart},{newLen} @@");
            for (var k = start; k < end; k++) sb.Append(ops[k].Kind).AppendLine(ops[k].Text);
            i = end;
        }
        return sb.ToString();
    }

    private static string[] Split(string s) =>
        s.Length == 0 ? Array.Empty<string>() : s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private readonly record struct Op(char Kind, string Text);

    private static List<Op> Ops(string[] a, string[] b)
    {
        var n = a.Length; var m = b.Length;
        var ops = new List<Op>();
        if ((long)n * m > 16_000_000)
        {
            // too big for the LCS table: show a whole-file replacement instead of exhausting memory
            foreach (var s in a) ops.Add(new Op('-', s));
            foreach (var s in b) ops.Add(new Op('+', s));
            return ops;
        }
        var l = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(new Op(' ', a[x])); x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) ops.Add(new Op('-', a[x++]));
            else ops.Add(new Op('+', b[y++]));
        }
        while (x < n) ops.Add(new Op('-', a[x++]));
        while (y < m) ops.Add(new Op('+', b[y++]));
        return ops;
    }
}
