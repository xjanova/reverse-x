using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReverseX.Licensing;

/// <summary>The xman studio product API, as tests can fake it.</summary>
internal interface IXmanClient
{
    Task<ApiReply> PostAsync(string path, object body);
    Task<ApiReply> GetAsync(string pathAndQuery);
}

/// <summary>
/// https://xman4289.com/api/v1/product/reverse-x/... — the same endpoints Anti X, WinXTools, CluadeX and BrainX use.
/// Only the pinned certificate chain is trusted, so a hosts-file redirect plus a planted root CA cannot
/// impersonate the license server.
/// </summary>
internal sealed class XmanApi : IXmanClient, IDisposable
{
    public const string HostName = "xman4289.com";
    public const string Host = "https://" + HostName;
    public const string ProductSlug = "reverse-x";
    public const string ProductApi = Host + "/api/v1/product/" + ProductSlug;
    public const string ProductPageUrl = Host + "/products/" + ProductSlug;

    // SubjectPublicKeyInfo SHA-256 pins above the leaf (it rotates every few months). Same chain as
    // Anti X / WinXTools / BrainX: leaf ← WE1 ← GTS Root R4 (cross-signed by GlobalSign Root CA).
    private static readonly HashSet<string> PinnedSpkiSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        "908769E8D34477CC2CBA0632C88605B22D7294C0840F78596D247C645B1AFC0E", // WE1 (Google Trust Services)
        "9847E5653E5E9E847516E5CB818606AA7544A19BE67FD7366D506988E8D84347", // GTS Root R4
        "2BCEE858158CF5465FC9D76F0DFA312FEF25A4DCA8501DA9B46B67D1FBFA1B64", // GlobalSign Root CA (R4 cross-sign)
    };

    private readonly HttpClient _http;

    public XmanApi(string userAgent) => _http = CreateClient(userAgent, TimeSpan.FromSeconds(20));

    public static HttpClient CreateClient(string userAgent, TimeSpan timeout, bool followRedirects = true)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            AllowAutoRedirect = followRedirects,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    /// <summary>True for an https:// address on xman4289.com itself (default port).</summary>
    public static bool IsXmanUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None || cert == null) return false;
        if (IsPinned(cert)) return true;
        if (chain?.ChainElements == null) return false;
        foreach (var element in chain.ChainElements)
            if (IsPinned(element.Certificate)) return true;
        return false;
    }

    private static bool IsPinned(X509Certificate2 cert) =>
        PinnedSpkiSha256.Contains(Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())));

    public Task<ApiReply> PostAsync(string path, object body) => SendAsync(() => _http.PostAsJsonAsync(ProductApi + path, body));

    public Task<ApiReply> GetAsync(string pathAndQuery) => SendAsync(() => _http.GetAsync(ProductApi + pathAndQuery));

    private static async Task<ApiReply> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send().ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length <= 1024 * 1024) json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // HTML from a proxy / 503 page: no answer from the license app itself
            }
            return new ApiReply(response.StatusCode, json as JsonObject, response.Headers.Date);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ApiReply.Unreachable;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One answer from the xman API; <see cref="Json"/> is null when the body was not JSON.</summary>
internal sealed class ApiReply(HttpStatusCode? status, JsonObject? json, DateTimeOffset? serverDate = null)
{
    public static readonly ApiReply Unreachable = new(null, null);

    public HttpStatusCode? Status { get; } = status;
    public JsonObject? Json { get; } = json;
    /// <summary>The server's clock (HTTP Date header) — dates are judged by it, never by the PC's clock.</summary>
    public DateTimeOffset? ServerDate { get; } = serverDate;

    public bool Success => Json.Bool("success") == true;
    public string? ErrorCode => Json.Str("error_code") ?? Json.Str("code");
    public JsonObject? Data => Json?["data"] as JsonObject;

    /// <summary>A real answer from the license app (JSON on 2xx/4xx except 429). Outages never change a license.</summary>
    public bool IsDefinitive => Json != null && Status is { } s && (int)s < 500 && s != HttpStatusCode.TooManyRequests;

    public string? ServerMessage
    {
        get
        {
            var message = Json.Str("message") ?? Json.Str("error");
            if (!string.IsNullOrWhiteSpace(message)) return message;
            if (Json?["errors"] is JsonObject errors)
                return string.Join(" ", errors.SelectMany(kv => kv.Value is JsonArray list ? list.Select(v => v?.ToString()) : new[] { kv.Value?.ToString() })
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            return null;
        }
    }
}

/// <summary>Lenient readers: the server mixes bools, 0/1 and strings for the same idea.</summary>
internal static class JsonRead
{
    public static string? Str(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
    }

    public static bool? Bool(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            JsonValueKind.String => value.GetValue<string>() is "1" or "true" or "True",
            _ => null,
        };
    }

    public static long? Long(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => (long)Math.Floor(value.GetValue<double>()),
            JsonValueKind.String when long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    /// <summary>Laravel dates end in "Z"; parse as UTC (DateTime.TryParse would shift them to local time).</summary>
    public static DateTime? Date(this JsonNode? node, string name) =>
        DateTimeOffset.TryParse(node.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
}
