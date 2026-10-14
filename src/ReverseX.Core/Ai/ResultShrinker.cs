using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReverseX.Core.Ai;

/// <summary>Keeps the full text of big tool results so the model can read them page by page (get_result_page).</summary>
public sealed class ResultStore
{
    private readonly Dictionary<string, string> _items = new();
    private int _next;

    public string Put(string full)
    {
        var id = "r" + (++_next);
        _items[id] = full;
        return id;
    }

    public string? Get(string id) => _items.TryGetValue(id, out var v) ? v : null;
}

/// <summary>
/// Makes a tool result small enough to send back to the model: JSON is compacted (long arrays and strings cut, with a marker),
/// and anything still over the limit is cut at a line boundary. The untouched original stays in the <see cref="ResultStore"/>.
/// </summary>
public static class ResultShrinker
{
    public const int DefaultPageChars = 12_000;
    private const int MaxArrayItems = 25;
    private const int MaxStringChars = 400;
    private const int MaxDepth = 8;

    public static string Shrink(string text, ResultStore store, int pageChars = DefaultPageChars)
    {
        if (text.Length <= pageChars) return text;
        var id = store.Put(text);
        var pages = PageCount(text.Length, pageChars);

        var compact = TryCompactJson(text);
        if (compact is not null && compact.Length <= pageChars)
            return compact + $"\n[ย่อแล้ว: อาร์เรย์/ข้อความยาวถูกตัด · ต้นฉบับ {text.Length:N0} อักขระ · อ่านเต็มทีละหน้าด้วย get_result_page(result_id=\"{id}\", page=1..{pages})]";

        var basis = compact ?? text;
        var head = CutAtLine(basis, pageChars);
        return head + $"\n[ผลลัพธ์ใหญ่: แสดงส่วนต้น {head.Length:N0} จาก {text.Length:N0} อักขระ · อ่านหน้าอื่นของต้นฉบับด้วย get_result_page(result_id=\"{id}\", page=1..{pages}) หรือถามเจาะจงขึ้น]";
    }

    public static string Page(ResultStore store, string id, int page, int pageChars = DefaultPageChars)
    {
        var full = store.Get(id);
        if (full is null) return $"ไม่พบ result_id \"{id}\"";
        var pages = PageCount(full.Length, pageChars);
        if (page < 1 || page > pages) return $"หน้าไม่ถูกต้อง: มี {pages} หน้า (เริ่มที่ 1)";
        var start = (page - 1) * pageChars;
        var len = Math.Min(pageChars, full.Length - start);
        return full.Substring(start, len) + $"\n[หน้า {page}/{pages}]";
    }

    public static int PageCount(int total, int pageChars) => Math.Max(1, (total + pageChars - 1) / pageChars);

    private static string CutAtLine(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf('\n', max - 1, Math.Min(max, 2000));
        return s[..(cut > 0 ? cut : max)];
    }

    private static string? TryCompactJson(string text)
    {
        var t = text.AsSpan().TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return null;
        try
        {
            var node = JsonNode.Parse(text);
            return node is null ? null : Compact(node, 0)?.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException) { return null; }
    }

    private static JsonNode? Compact(JsonNode? n, int depth)
    {
        switch (n)
        {
            case JsonArray a:
                if (depth >= MaxDepth) return JsonValue.Create($"[อาร์เรย์ {a.Count} รายการ]");
                var arr = new JsonArray();
                for (var i = 0; i < a.Count && i < MaxArrayItems; i++) arr.Add(Compact(a[i], depth + 1));
                if (a.Count > MaxArrayItems) arr.Add(JsonValue.Create($"…(อีก {a.Count - MaxArrayItems} รายการ)"));
                return arr;
            case JsonObject o:
                if (depth >= MaxDepth) return JsonValue.Create($"{{ออบเจ็กต์ {o.Count} คีย์}}");
                var obj = new JsonObject();
                foreach (var kv in o) obj[kv.Key] = Compact(kv.Value, depth + 1);
                return obj;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > MaxStringChars:
                return JsonValue.Create(s[..MaxStringChars] + $"…(+{s.Length - MaxStringChars} อักขระ)");
            default:
                return n?.DeepClone();
        }
    }
}
