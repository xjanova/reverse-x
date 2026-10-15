using System.Text.RegularExpressions;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Finds URLs and credential-looking strings in text pulled out of a target. Secrets are masked before they leave here.</summary>
public static partial class TextScan
{
    [GeneratedRegex(@"\b(?:https?|wss?)://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRx();

    private static readonly (string Kind, Regex Rx)[] SecretPatterns =
    {
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.CultureInvariant)),
        ("AWS access key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("Private key", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("Stripe live key", new Regex(@"\b[sr]k_live_[0-9A-Za-z]{20,}\b", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex(@"\bxox[abprs]-[0-9A-Za-z\-]{10,}\b", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex(@"\b(?:ghp|gho|ghs|ghu|github_pat)_[0-9A-Za-z_]{30,}\b", RegexOptions.CultureInvariant)),
        ("JWT", new Regex(@"\beyJ[0-9A-Za-z_\-]{8,}\.eyJ[0-9A-Za-z_\-]{8,}\.[0-9A-Za-z_\-]{8,}\b", RegexOptions.CultureInvariant)),
        ("Firebase database", new Regex(@"\b[a-z0-9\-]+\.firebaseio\.com\b", RegexOptions.CultureInvariant)),
        ("OpenAI key", new Regex(@"\bsk-(?:proj-)?[0-9A-Za-z_\-]{32,}\b", RegexOptions.CultureInvariant)),
    };

    public static IEnumerable<string> Urls(string text)
    {
        if (text.Length < 10 || text.IndexOf("://", StringComparison.Ordinal) < 0) yield break;
        foreach (Match m in UrlRx().Matches(text))
        {
            var u = m.Value.TrimEnd('.', ',', ';', ')', '\'', ']');
            if (u.Length > 10 && !IsBoilerplate(u)) yield return u.Length > 300 ? u[..300] : u;
        }
    }

    // Schema/namespace URLs every app carries; they say nothing about the target.
    private static bool IsBoilerplate(string u) =>
        u.Contains("schemas.android.com", StringComparison.OrdinalIgnoreCase)
        || u.Contains("www.w3.org/", StringComparison.OrdinalIgnoreCase)
        || u.Contains("schemas.microsoft.com", StringComparison.OrdinalIgnoreCase)
        || u.Contains("ns.adobe.com", StringComparison.OrdinalIgnoreCase)
        || u.Contains("xmlpull.org", StringComparison.OrdinalIgnoreCase)
        || u.Contains("apache.org/licenses", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<SecretHit> Secrets(string text, string where)
    {
        if (text.Length < 16) yield break;
        foreach (var (kind, rx) in SecretPatterns)
            foreach (Match m in rx.Matches(text))
                yield return new SecretHit(kind, Mask(m.Value), where);
    }

    public static string Mask(string value)
    {
        if (value.StartsWith("-----", StringComparison.Ordinal) || value.EndsWith(".firebaseio.com", StringComparison.Ordinal)) return value;
        if (value.Length <= 12) return new string('•', value.Length);
        return value[..6] + "…" + value[^4..] + $" ({value.Length})";
    }

    /// <summary>Collects unique, capped results while scanning many strings.</summary>
    public sealed class Collector
    {
        private readonly HashSet<string> _urls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _secretKeys = new(StringComparer.Ordinal);
        private readonly int _urlCap;
        private readonly int _secretCap;
        public List<string> UrlList { get; } = new();
        public List<SecretHit> SecretList { get; } = new();

        public Collector(int urlCap = 500, int secretCap = 100) { _urlCap = urlCap; _secretCap = secretCap; }

        public void Scan(string text, string where)
        {
            if (UrlList.Count < _urlCap)
                foreach (var u in Urls(text))
                    if (_urls.Add(u)) { UrlList.Add(u); if (UrlList.Count >= _urlCap) break; }
            if (SecretList.Count < _secretCap)
                foreach (var s in Secrets(text, where))
                    if (_secretKeys.Add(s.Kind + s.Masked)) { SecretList.Add(s); if (SecretList.Count >= _secretCap) break; }
        }
    }
}
