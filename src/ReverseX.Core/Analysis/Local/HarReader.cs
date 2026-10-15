using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReverseX.Core.Analysis.Local;

/// <summary>
/// Reads a HAR (HTTP Archive) capture: requests, hosts, endpoint templates and trackers.
/// Credential headers, cookies and token-like query values are redacted before anything is kept.
/// </summary>
public static partial class HarReader
{
    public const int EntryCap = 5000;
    public const int BodyCap = 4000;

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token",
        "x-csrf-token", "x-xsrf-token", "x-amz-security-token", "x-goog-api-key", "api-key",
    };

    [GeneratedRegex(@"(token|key|secret|pass(word)?|auth|session|sig(nature)?|code|jwt|bearer)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParamRx();

    [GeneratedRegex(@"^(\d+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{16,}|[A-Za-z0-9_\-]{24,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdSegmentRx();

    private static readonly (string Suffix, string Name)[] Trackers =
    {
        ("google-analytics.com", "Google Analytics"), ("analytics.google.com", "Google Analytics"),
        ("googletagmanager.com", "Google Tag Manager"), ("doubleclick.net", "DoubleClick"),
        ("googlesyndication.com", "Google Ads"), ("googleadservices.com", "Google Ads"),
        ("facebook.net", "Meta Pixel"), ("facebook.com/tr", "Meta Pixel"), ("connect.facebook.net", "Meta Pixel"),
        ("hotjar.com", "Hotjar"), ("clarity.ms", "Microsoft Clarity"), ("bat.bing.com", "Bing Ads"),
        ("segment.io", "Segment"), ("segment.com", "Segment"), ("mixpanel.com", "Mixpanel"),
        ("amplitude.com", "Amplitude"), ("sentry.io", "Sentry"), ("newrelic.com", "New Relic"),
        ("nr-data.net", "New Relic"), ("datadoghq.com", "Datadog"), ("browser-intake-datadoghq.com", "Datadog"),
        ("tiktok.com/i18n/pixel", "TikTok Pixel"), ("analytics.tiktok.com", "TikTok Pixel"),
        ("snap.licdn.com", "LinkedIn Insight"), ("ads.linkedin.com", "LinkedIn Ads"),
        ("criteo.com", "Criteo"), ("taboola.com", "Taboola"), ("outbrain.com", "Outbrain"),
        ("scorecardresearch.com", "comScore"), ("quantserve.com", "Quantcast"), ("adnxs.com", "AppNexus"),
        ("app-measurement.com", "Firebase Analytics"), ("appsflyer.com", "AppsFlyer"), ("adjust.com", "Adjust"),
        ("branch.io", "Branch"), ("onesignal.com", "OneSignal"), ("intercom.io", "Intercom"),
        ("fullstory.com", "FullStory"), ("mouseflow.com", "Mouseflow"), ("heap.io", "Heap"),
        ("heapanalytics.com", "Heap"), ("posthog.com", "PostHog"), ("plausible.io", "Plausible"),
        ("yandex.ru/metrika", "Yandex Metrica"), ("mc.yandex.ru", "Yandex Metrica"),
    };

    public static string? TrackerFor(string hostAndPath)
    {
        foreach (var (suffix, name) in Trackers)
        {
            var slash = suffix.IndexOf('/');
            if (slash < 0)
            {
                var host = hostAndPath.Split('/')[0];
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            else if (hostAndPath.Contains(suffix, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    public static HarModel Read(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new AnalysisProgress("har.parse", 0, 3));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        if (!doc.RootElement.TryGetProperty("log", out var log) || log.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("not a HAR file (no \"log\" object)");

        var creator = Str(log, "creator", "name") is { } cn ? cn + (Str(log, "creator", "version") is { } cv ? " " + cv : "") : "";
        var browser = Str(log, "browser", "name") is { } bn ? bn + (Str(log, "browser", "version") is { } bv ? " " + bv : "") : null;
        var pages = log.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Array ? pg.GetArrayLength() : 0;

        var redacted = 0;
        var entries = new List<HarEntry>();
        var total = 0;
        DateTimeOffset? first = null;
        double spanEnd = 0;
        var startTimes = new List<(DateTimeOffset Start, int Idx)>();

        progress?.Report(new AnalysisProgress("har.entries", 1, 3));
        if (log.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in es.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                total++;
                if (entries.Count >= EntryCap) continue;
                if (!e.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object) continue;
                var url = Str(req, "url") ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                var reqHeaders = Headers(req, ref redacted, out var reqCred);
                var resHeaders = new List<NameValue>();
                var resCred = false;
                int status = 0; string? statusText = null, mime = null, resBody = null, httpVer = null;
                long resBytes = 0;
                if (e.TryGetProperty("response", out var res) && res.ValueKind == JsonValueKind.Object)
                {
                    status = Int(res, "status");
                    statusText = Str(res, "statusText");
                    httpVer = Str(res, "httpVersion");
                    resHeaders = Headers(res, ref redacted, out resCred);
                    if (res.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
                    {
                        mime = Str(c, "mimeType")?.Split(';')[0].Trim();
                        resBytes = Math.Max(Long(c, "size"), 0);
                        if (Str(c, "text") is { } t && Str(c, "encoding") != "base64" && IsTextMime(mime))
                            resBody = Clip(TextScanRedact(t, ref redacted));
                    }
                    var bs = Long(res, "bodySize");
                    if (resBytes == 0 && bs > 0) resBytes = bs;
                }

                var query = new List<NameValue>();
                if (req.TryGetProperty("queryString", out var qs) && qs.ValueKind == JsonValueKind.Array)
                    foreach (var q in qs.EnumerateArray())
                    {
                        var n = Str(q, "name") ?? ""; var v = Str(q, "value") ?? "";
                        if (v.Length > 0 && SecretParamRx().IsMatch(n)) { v = TextScan.Mask(v); redacted++; }
                        query.Add(new NameValue(n, v));
                    }

                string? reqBody = null;
                if (req.TryGetProperty("postData", out var pd) && pd.ValueKind == JsonValueKind.Object && Str(pd, "text") is { } pt)
                    reqBody = Clip(TextScanRedact(pt, ref redacted));

                var time = Dbl(e, "time");
                var started = Str(e, "startedDateTime");
                if (started != null && DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var st))
                {
                    startTimes.Add((st, entries.Count));
                    if (first == null || st < first) first = st;
                }

                var hdrSize = Long(req, "headersSize");
                var bodySize = Long(req, "bodySize");
                entries.Add(new HarEntry
                {
                    Index = entries.Count,
                    Method = (Str(req, "method") ?? "GET").ToUpperInvariant(),
                    Url = RedactUrl(uri, ref redacted),
                    Host = uri.Host,
                    Path = uri.AbsolutePath,
                    Template = Template(uri.AbsolutePath),
                    Status = status,
                    StatusText = statusText,
                    MimeType = string.IsNullOrEmpty(mime) ? null : mime,
                    ResponseBytes = resBytes,
                    RequestBytes = Math.Max(hdrSize, 0) + Math.Max(bodySize, 0),
                    TimeMs = time,
                    ServerIp = Str(e, "serverIPAddress")?.Trim('[', ']'),
                    HttpVersion = httpVer,
                    RequestHeaders = reqHeaders,
                    ResponseHeaders = resHeaders,
                    Query = query,
                    RequestBody = reqBody,
                    ResponseBody = resBody,
                    HasCredentials = reqCred || resCred,
                });
            }
        }

        // Start offsets relative to the first request, so the UI can draw a waterfall.
        if (first is { } f0)
            foreach (var (st, idx) in startTimes)
            {
                var off = (st - f0).TotalMilliseconds;
                entries[idx] = entries[idx] with { StartMs = off };
                spanEnd = Math.Max(spanEnd, off + Math.Max(entries[idx].TimeMs, 0));
            }

        progress?.Report(new AnalysisProgress("har.group", 2, 3));
        var hosts = entries.GroupBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HarHost(g.Key, g.Count(), g.Sum(x => x.ResponseBytes),
                g.All(x => x.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || x.Url.StartsWith("wss:", StringComparison.OrdinalIgnoreCase)),
                g.Count(x => x.Status >= 400 || x.Status == 0),
                TrackerFor(g.Key) ?? g.Select(x => TrackerFor(x.Host + x.Path)).FirstOrDefault(t => t != null)))
            .OrderByDescending(h => h.Requests).ToList();

        var endpoints = entries.GroupBy(x => (x.Method, Host: x.Host.ToLowerInvariant(), x.Template))
            .Select(g => new HarEndpoint(g.Key.Method, g.Key.Host, g.Key.Template, g.Count(), g.Count(x => x.Status >= 400),
                Math.Round(g.Average(x => Math.Max(x.TimeMs, 0)), 1),
                g.Select(x => x.MimeType).FirstOrDefault(m => m != null)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Host, StringComparer.Ordinal).ThenBy(x => x.Template, StringComparer.Ordinal).ToList();

        var mimes = entries.GroupBy(x => x.MimeType ?? "?").Select(g => new NamedCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ToList();

        var model = new HarModel
        {
            Creator = creator,
            Browser = browser,
            Pages = pages,
            TotalEntries = total,
            Entries = entries,
            Hosts = hosts,
            Endpoints = endpoints,
            Errors4xx = entries.Count(x => x.Status is >= 400 and < 500),
            Errors5xx = entries.Count(x => x.Status >= 500),
            TotalBytes = entries.Sum(x => x.ResponseBytes),
            SpanMs = Math.Round(spanEnd, 1),
            RedactedValues = redacted,
            MimeTypes = mimes,
        };
        progress?.Report(new AnalysisProgress("har.group", 3, 3));
        return model with { Insights = Insights(model) };
    }

    /// <summary>Turns /v1/users/12345/orders/9f86d08… into /v1/users/{id}/orders/{id}.</summary>
    public static string Template(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0 && IdSegmentRx().IsMatch(parts[i]) && !parts[i].Contains('.')) parts[i] = "{id}";
        return string.Join('/', parts);
    }

    private static string RedactUrl(Uri uri, ref int redacted)
    {
        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?") return uri.GetLeftPart(UriPartial.Path);
        var pairs = uri.Query.TrimStart('?').Split('&');
        for (var i = 0; i < pairs.Length; i++)
        {
            var eq = pairs[i].IndexOf('=');
            if (eq <= 0) continue;
            var name = Uri.UnescapeDataString(pairs[i][..eq]);
            if (SecretParamRx().IsMatch(name) && eq + 1 < pairs[i].Length)
            {
                pairs[i] = pairs[i][..(eq + 1)] + "•••";
                redacted++;
            }
        }
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', pairs);
    }

    private static List<NameValue> Headers(JsonElement owner, ref int redacted, out bool hadCredential)
    {
        hadCredential = false;
        var list = new List<NameValue>();
        if (!owner.TryGetProperty("headers", out var hs) || hs.ValueKind != JsonValueKind.Array) return list;
        foreach (var h in hs.EnumerateArray())
        {
            var n = Str(h, "name") ?? ""; var v = Str(h, "value") ?? "";
            if (SecretHeaders.Contains(n) && v.Length > 0)
            {
                hadCredential = true;
                redacted++;
                // Keep the scheme ("Bearer") and cookie names; they explain the request without leaking the value.
                v = n.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                    ? string.Join("; ", v.Split(';').Select(c => c.Split('=')[0].Trim() + "=•••"))
                    : n.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)
                        ? CookieShape(v)
                        : v.Contains(' ') ? v[..v.IndexOf(' ')] + " •••" : "•••";
            }
            list.Add(new NameValue(n, v));
        }
        return list;
    }

    /// <summary>"sid=abc; Path=/; HttpOnly" → "sid=•••; Path=/; HttpOnly" (attributes kept for the cookie checks).</summary>
    private static string CookieShape(string v)
    {
        var parts = v.Split(';');
        var eq = parts[0].IndexOf('=');
        parts[0] = (eq > 0 ? parts[0][..eq] : parts[0]).Trim() + "=•••";
        return string.Join(";", parts);
    }

    private static string TextScanRedact(string text, ref int redacted)
    {
        foreach (var s in TextScan.Secrets(text, "har").ToList())
        {
            // Secrets() returns masked values; find the original by pattern again isn't possible, so mask whole tokens that start the same.
            var prefix = s.Masked.Split('…')[0];
            if (prefix.Length < 4 || prefix.StartsWith("-----", StringComparison.Ordinal)) continue;
            var i = text.IndexOf(prefix, StringComparison.Ordinal);
            while (i >= 0)
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-' or '.')) end++;
                text = text[..i] + TextScan.Mask(text[i..end]) + text[end..];
                redacted++;
                i = text.IndexOf(prefix, i + 1, StringComparison.Ordinal);
                if (i >= 0 && text.AsSpan(i).StartsWith(prefix + "…")) i = text.IndexOf(prefix, i + 1, StringComparison.Ordinal);
            }
        }
        return text;
    }

    private static bool IsTextMime(string? mime) => mime != null &&
        (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase)
         || mime.Contains("xml", StringComparison.OrdinalIgnoreCase) || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)
         || mime.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

    private static string Clip(string s) => s.Length <= BodyCap ? s : s[..BodyCap] + "\n…";

    private static List<Insight> Insights(HarModel m)
    {
        var list = new List<Insight>();
        if (m.TotalEntries > m.Entries.Count) list.Add(Insight.Of("HarCapped", InsightTone.Info, EvidenceLevel.Seen, m.Entries.Count, m.TotalEntries));
        var trackers = m.Hosts.Where(h => h.Tracker != null).Select(h => h.Tracker!).Distinct().ToList();
        if (trackers.Count > 0) list.Add(Insight.Of("HarTrackers", InsightTone.Warn, EvidenceLevel.Seen, trackers.Count, string.Join(", ", trackers)));
        var plain = m.Entries.Where(e => e.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                                         && e.Host is not ("localhost" or "127.0.0.1" or "[::1]")).Select(e => e.Host).Distinct().ToList();
        if (plain.Count > 0) list.Add(Insight.Of("HarPlainHttp", InsightTone.Risk, EvidenceLevel.Seen, plain.Count, string.Join(", ", plain.Take(5))));
        var plainCreds = m.Entries.Count(e => e.HasCredentials && e.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase));
        if (plainCreds > 0) list.Add(Insight.Of("HarCredsOverHttp", InsightTone.Risk, EvidenceLevel.Seen, plainCreds));

        var weakCookies = m.Entries.SelectMany(e => e.ResponseHeaders)
            .Where(h => h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            .Where(h => !h.Value.Contains("secure", StringComparison.OrdinalIgnoreCase) || !h.Value.Contains("httponly", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value.Split('=')[0]).Distinct().ToList();
        if (weakCookies.Count > 0) list.Add(Insight.Of("HarWeakCookies", InsightTone.Warn, EvidenceLevel.Seen, weakCookies.Count, string.Join(", ", weakCookies.Take(8))));

        var corsAny = m.Entries.Any(e => e.ResponseHeaders.Any(h => h.Name.Equals("access-control-allow-origin", StringComparison.OrdinalIgnoreCase) && h.Value.Trim() == "*")
                                         && e.ResponseHeaders.Any(h => h.Name.Equals("access-control-allow-credentials", StringComparison.OrdinalIgnoreCase) && h.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));
        if (corsAny) list.Add(Insight.Of("HarCorsWildcard", InsightTone.Risk, EvidenceLevel.Seen));

        if (m.Errors5xx > 0) list.Add(Insight.Of("HarServerErrors", InsightTone.Warn, EvidenceLevel.Seen, m.Errors5xx));
        if (m.Errors4xx > 0) list.Add(Insight.Of("HarClientErrors", InsightTone.Info, EvidenceLevel.Seen, m.Errors4xx));
        var gql = m.Endpoints.Count(e => e.Template.Contains("graphql", StringComparison.OrdinalIgnoreCase));
        if (gql > 0) list.Add(Insight.Of("HarGraphQl", InsightTone.Info, EvidenceLevel.Inferred, gql));
        if (m.Entries.Any(e => e.Url.StartsWith("ws", StringComparison.OrdinalIgnoreCase)))
            list.Add(Insight.Of("HarWebSocket", InsightTone.Info, EvidenceLevel.Seen));
        if (m.RedactedValues > 0) list.Add(Insight.Of("HarRedacted", InsightTone.Good, EvidenceLevel.Seen, m.RedactedValues));
        return list;
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static int Int(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : -1;

    private static double Dbl(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
