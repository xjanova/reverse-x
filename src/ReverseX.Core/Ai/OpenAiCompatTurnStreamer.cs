using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReverseX.Core.Ai;

/// <summary>A non-2xx answer from a vendor endpoint. The body goes to the log only, never to the screen.</summary>
public sealed class AiHttpException : Exception
{
    public int Status { get; }
    public AiHttpException(int status, string body) : base($"HTTP {status}: {(body.Length > 400 ? body[..400] : body)}") => Status = status;
}

/// <summary>
/// Streams one turn over the OpenAI "chat/completions" dialect: OpenAI, Gemini, OpenRouter, DeepSeek, Groq, Mistral, xAI, Together,
/// Ollama, LM Studio and any gateway that speaks it. Tool calls, streamed text and reasoning text are mapped to the SDK-neutral types.
/// </summary>
public sealed class OpenAiCompatTurnStreamer : IModelStreamer
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _baseUrl;
    private readonly bool _needsKey;
    private readonly bool _maxCompletionTokens;
    private readonly Func<string?> _key;

    public OpenAiCompatTurnStreamer(string baseUrl, Func<string?> keyProvider, bool needsKey, bool useMaxCompletionTokens)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _key = keyProvider;
        _needsKey = needsKey;
        _maxCompletionTokens = useMaxCompletionTokens;
    }

    public async Task<TurnResult> StreamTurnAsync(TurnRequest req, Action<string> onText, Action<string> onThinking, CancellationToken ct)
    {
        var key = _key();
        if (_needsKey && string.IsNullOrWhiteSpace(key)) throw new NoApiKeyException();
        if (!AiProviders.IsAcceptableUrl(_baseUrl)) throw new InvalidOperationException("endpoint must be https (or http on this machine / LAN)");

        using var msg = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(BuildBody(req).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiHttpException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, ToolAcc>();
        string? finish = null;
        long promptTok = 0, outTok = 0, cached = 0;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }
            if (chunk is null) continue;
            if (chunk["error"] is { } err) throw new AiHttpException(500, err.ToJsonString());

            if (chunk["usage"] is JsonObject u)
            {
                promptTok = Num(u["prompt_tokens"]);
                outTok = Num(u["completion_tokens"]);
                cached = Num(u["prompt_tokens_details"]?["cached_tokens"]);
            }

            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            if (choice["finish_reason"] is JsonValue fv && fv.TryGetValue<string>(out var fr)) finish = fr;
            if (choice["delta"] is not JsonObject delta) continue;

            if (delta["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > 0) { text.Append(c); onText(c); }
            var reasoning = delta["reasoning_content"] ?? delta["reasoning"];
            if (reasoning is JsonValue rv && rv.TryGetValue<string>(out var r) && r.Length > 0) onThinking(r);

            if (delta["tool_calls"] is JsonArray tcs)
            {
                foreach (var tcNode in tcs)
                {
                    if (tcNode is not JsonObject tc) continue;
                    var idx = tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var ii) ? ii : calls.Count;
                    if (!calls.TryGetValue(idx, out var acc)) calls[idx] = acc = new ToolAcc();
                    if (tc["id"] is JsonValue idv && idv.TryGetValue<string>(out var id) && id.Length > 0) acc.Id = id;
                    if (tc["function"] is JsonObject fn)
                    {
                        if (fn["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && n.Length > 0) acc.Name = n;
                        if (fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var a)) acc.Args.Append(a);
                    }
                }
            }
        }

        var blocks = new List<AiBlock>();
        if (text.Length > 0) blocks.Add(new AiText(text.ToString()));
        var n2 = 0;
        foreach (var acc in calls.Values)
        {
            if (string.IsNullOrEmpty(acc.Name)) continue;
            blocks.Add(new AiToolUse(string.IsNullOrEmpty(acc.Id) ? "call_" + (++n2) : acc.Id, acc.Name, NormalizeArgs(acc.Args.ToString())));
        }

        var hasCalls = blocks.Any(b => b is AiToolUse);
        var stop = finish switch
        {
            "length" => AiStop.MaxTokens,
            "content_filter" => AiStop.Refusal,
            _ when hasCalls => AiStop.ToolUse, // some local servers say "stop" even with tool calls
            _ => AiStop.EndTurn,
        };
        var usage = new AiUsage(Math.Max(0, promptTok - cached), outTok, 0, 0, cached);
        return new TurnResult(new AiMessage(AiRole.Assistant, blocks), stop, usage);
    }

    private JsonObject BuildBody(TurnRequest req)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = req.System } };
        foreach (var m in req.Messages) AppendMessage(messages, m);

        var body = new JsonObject
        {
            ["model"] = req.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            [_maxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = req.MaxTokens,
        };
        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
            {
                JsonNode? schema;
                try { schema = JsonNode.Parse(t.InputSchemaJson); } catch (JsonException) { schema = null; }
                schema ??= new JsonObject();
                if (schema is JsonObject so && !so.ContainsKey("type")) so["type"] = "object";
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = schema },
                });
            }
            body["tools"] = tools;
        }
        return body;
    }

    private static void AppendMessage(JsonArray messages, AiMessage m)
    {
        if (m.Role == AiRole.User)
        {
            foreach (var tr in m.Blocks.OfType<AiToolResult>())
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tr.ToolUseId, ["content"] = tr.IsError ? "ERROR: " + tr.Content : tr.Content });
            var userText = string.Concat(m.Blocks.OfType<AiText>().Select(t => t.Text));
            if (userText.Length > 0) messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
            return;
        }

        var a = new JsonObject { ["role"] = "assistant" };
        var asText = string.Concat(m.Blocks.OfType<AiText>().Select(t => t.Text));
        a["content"] = asText.Length > 0 ? asText : null;
        var uses = m.Blocks.OfType<AiToolUse>().ToList();
        if (uses.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var u in uses)
                arr.Add(new JsonObject
                {
                    ["id"] = u.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = u.Name, ["arguments"] = u.InputJson },
                });
            a["tool_calls"] = arr;
        }
        if (asText.Length == 0 && uses.Count == 0) return;
        messages.Add(a);
    }

    private static string NormalizeArgs(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        try { return JsonNode.Parse(s) is JsonObject ? s : "{}"; }
        catch (JsonException) { return "{}"; }
    }

    private static long Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private sealed class ToolAcc
    {
        public string? Id, Name;
        public readonly StringBuilder Args = new();
    }
}
