using System.Text.RegularExpressions;

namespace ReverseX.Core.Download;

/// <summary>Pulls an Android package id out of a Google Play link (or a bare package name).</summary>
public static partial class PlayLink
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z")]
    private static partial Regex PackageRegex();

    // Windows treats "con.x.apk" etc. as a device name, so the id (used as a file/folder name) must not start with one.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static bool IsValidPackageId(string s) =>
        s.Length <= 200 && PackageRegex().IsMatch(s) && !ReservedNames.Contains(s[..s.IndexOf('.')]);

    /// <summary>Returns the package id, or null when the text is not a recognisable Play link / package name.</summary>
    public static string? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (IsValidPackageId(text)) return text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var isPlay = uri.Scheme is "http" or "https"
            ? host is "play.google.com" or "market.android.com"
            : uri.Scheme == "market";
        if (!isPlay) return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i <= 0 || !pair.AsSpan(0, i).Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Uri.UnescapeDataString(pair[(i + 1)..]);
            return IsValidPackageId(id) ? id : null;
        }
        return null;
    }
}
