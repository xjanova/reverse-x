using System.Text.RegularExpressions;

namespace ReverseX.Core.Download;

/// <summary>Pulls an Android package id out of a Google Play link (or a bare package name).</summary>
public static partial class PlayLink
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z")]
    private static partial Regex PackageRegex();

    public static bool IsValidPackageId(string s) => s.Length <= 200 && PackageRegex().IsMatch(s);

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
