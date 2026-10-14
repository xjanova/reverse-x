using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace ReverseX.Core.Ai;

/// <summary>User-facing (Thai) text for failures; raw exception detail goes to the log only.</summary>
public static class ErrorText
{
    public static string For(Exception ex) => ex switch
    {
        NoApiKeyException => NoKey,
        AnthropicUnauthorizedException => "API key ไม่ถูกต้องหรือถูกยกเลิก — ตรวจ key ในหน้าตั้งค่า",
        AnthropicForbiddenException => "key นี้ไม่มีสิทธิ์ใช้โมเดลที่เลือก — ลองเลือกโมเดลอื่นในหน้าตั้งค่า",
        AnthropicRateLimitException => "ถูกจำกัดอัตราการเรียก (rate limit) — รอสักครู่แล้วลองใหม่",
        AnthropicNotFoundException => "ไม่พบโมเดลที่เลือกสำหรับ key นี้ — เลือกโมเดลอื่นในหน้าตั้งค่า",
        AnthropicBadRequestException => "คำขอไม่ถูกต้อง (บทสนทนาอาจยาวเกิน) — ลองล้างประวัติแล้วถามใหม่",
        Anthropic5xxException => "เซิร์ฟเวอร์ Claude ขัดข้องชั่วคราว — ลองใหม่อีกครั้ง",
        AnthropicIOException or HttpRequestException => "เชื่อมต่อ Claude ไม่ได้ — ตรวจอินเทอร์เน็ต",
        TimeoutException => "รอนานเกินไป — ลองใหม่อีกครั้ง",
        _ => "เกิดข้อผิดพลาดระหว่างถามผู้ช่วย AI — ลองใหม่ (รายละเอียดอยู่ในไฟล์ log)",
    };

    public const string NoKey = "ยังไม่ได้ใส่ API key — ไปที่เมนู “ตั้งค่า” วาง Anthropic API key (ขึ้นต้นด้วย sk-ant-) แล้วกดบันทึก";
}

public sealed class NoApiKeyException : Exception
{
    public NoApiKeyException() : base("no api key") { }
}

/// <summary>The only place that talks to Claude: the official Anthropic C# SDK (package "Anthropic"), streaming, adaptive thinking, cached system + tools.</summary>
public sealed class AnthropicTurnStreamer : IModelStreamer
{
    private readonly Func<string?> _key;

    /// <summary>keyProvider returns the key typed into Reverse X (ApiKeyStore); nothing is read from the environment.</summary>
    public AnthropicTurnStreamer(Func<string?> keyProvider) => _key = keyProvider;

    public async Task<TurnResult> StreamTurnAsync(TurnRequest req, Action<string> onText, Action<string> onThinking, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new NoApiKeyException();
        using var client = new AnthropicClient { ApiKey = key, AuthToken = null, BaseUrl = "https://api.anthropic.com" };

        var tools = new List<ToolUnion>();
        for (var i = 0; i < req.Tools.Count; i++)
        {
            var t = req.Tools[i];
            var tool = new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = ToSchema(t.InputSchemaJson),
            };
            if (i == req.Tools.Count - 1) tool = tool with { CacheControl = new CacheControlEphemeral() };
            tools.Add(tool);
        }

        var p = new MessageCreateParams
        {
            Model = req.Model,
            MaxTokens = req.MaxTokens,
            System = new List<TextBlockParam> { new() { Text = req.System, CacheControl = new CacheControlEphemeral() } },
            Messages = req.Messages.Select(ToParam).ToList(),
            Thinking = new ThinkingConfigAdaptive(),
            Tools = tools,
        };

        var blocks = new SortedDictionary<long, Acc>();
        long inTok = 0, outTok = 0, cw5 = 0, cw1 = 0, cr = 0;
        StopReason? stop = null;
        string? cat = null, expl = null;

        await foreach (var ev in client.Messages.CreateStreaming(p, ct))
        {
            if (ev.TryPickStart(out var start))
            {
                var u = start.Message.Usage;
                inTok = u.InputTokens;
                cr = u.CacheReadInputTokens ?? 0;
                (cw5, cw1) = CacheWrites(u);
                outTok = u.OutputTokens;
            }
            else if (ev.TryPickContentBlockStart(out var cs))
            {
                var a = new Acc();
                if (cs.ContentBlock.TryPickText(out var tb)) { a.Kind = 't'; a.Text.Append(tb.Text); }
                else if (cs.ContentBlock.TryPickThinking(out _)) a.Kind = 'k';
                else if (cs.ContentBlock.TryPickRedactedThinking(out var rb)) { a.Kind = 'r'; a.Text.Append(rb.Data); }
                else if (cs.ContentBlock.TryPickToolUse(out var tu)) { a.Kind = 'u'; a.Id = tu.ID; a.Name = tu.Name; }
                blocks[cs.Index] = a;
            }
            else if (ev.TryPickContentBlockDelta(out var cd))
            {
                if (!blocks.TryGetValue(cd.Index, out var a)) continue;
                if (cd.Delta.TryPickText(out var td)) { a.Text.Append(td.Text); onText(td.Text); }
                else if (cd.Delta.TryPickThinking(out var th)) { a.Text.Append(th.Thinking); onThinking(th.Thinking); }
                else if (cd.Delta.TryPickSignature(out var sg)) a.Signature = sg.Signature;
                else if (cd.Delta.TryPickInputJson(out var ij)) a.Json.Append(ij.PartialJson);
            }
            else if (ev.TryPickDelta(out var md))
            {
                try { stop = md.Delta.StopReason.Value(); } catch (Exception) { stop = null; }
                try { if (md.Delta.StopDetails is { } sd) { cat = sd.Category.Raw(); expl = sd.Explanation; } } catch (Exception) { /* details are optional */ }
                var u = md.Usage;
                outTok = u.OutputTokens;
                if (u.InputTokens is { } i2) inTok = i2;
                if (u.CacheReadInputTokens is { } r2) cr = r2;
                if (u.CacheCreationInputTokens is { } w2 && cw5 + cw1 == 0) cw5 = w2;
            }
        }

        var outBlocks = new List<AiBlock>();
        foreach (var a in blocks.Values)
        {
            switch (a.Kind)
            {
                case 't' when a.Text.Length > 0: outBlocks.Add(new AiText(a.Text.ToString())); break;
                case 'k': outBlocks.Add(new AiThinking(a.Text.ToString(), a.Signature ?? "")); break;
                case 'r': outBlocks.Add(new AiRedactedThinking(a.Text.ToString())); break;
                case 'u': outBlocks.Add(new AiToolUse(a.Id!, a.Name!, a.Json.Length == 0 ? "{}" : a.Json.ToString())); break;
            }
        }

        return new TurnResult(new AiMessage(AiRole.Assistant, outBlocks), Map(stop), new AiUsage(inTok, outTok, cw5, cw1, cr), cat, expl);
    }

    private static (long, long) CacheWrites(Usage u)
    {
        if (u.CacheCreation is { } cc) return (cc.Ephemeral5mInputTokens, cc.Ephemeral1hInputTokens);
        return (u.CacheCreationInputTokens ?? 0, 0);
    }

    private static AiStop Map(StopReason? s) => s switch
    {
        StopReason.EndTurn or StopReason.StopSequence => AiStop.EndTurn,
        StopReason.MaxTokens => AiStop.MaxTokens,
        StopReason.ToolUse => AiStop.ToolUse,
        StopReason.PauseTurn => AiStop.PauseTurn,
        StopReason.Refusal => AiStop.Refusal,
        StopReason.ModelContextWindowExceeded => AiStop.ContextExceeded,
        _ => AiStop.Other,
    };

    private sealed class Acc
    {
        public char Kind;
        public string? Id, Name, Signature;
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Json = new();
    }

    private static InputSchema ToSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raw = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject()) raw[prop.Name] = prop.Value.Clone();
        if (!raw.ContainsKey("type")) raw["type"] = JsonDocument.Parse("\"object\"").RootElement.Clone();
        return InputSchema.FromRawUnchecked(raw);
    }

    private static MessageParam ToParam(AiMessage m)
    {
        var list = new List<ContentBlockParam>();
        foreach (var b in m.Blocks)
        {
            switch (b)
            {
                case AiText t when t.Text.Length > 0: list.Add(new TextBlockParam { Text = t.Text }); break;
                case AiThinking k: list.Add(new ThinkingBlockParam { Thinking = k.Thinking, Signature = k.Signature }); break;
                case AiRedactedThinking r: list.Add(new RedactedThinkingBlockParam { Data = r.Data }); break;
                case AiToolUse u:
                    using (var doc = JsonDocument.Parse(u.InputJson))
                    {
                        var input = new Dictionary<string, JsonElement>();
                        foreach (var prop in doc.RootElement.EnumerateObject()) input[prop.Name] = prop.Value.Clone();
                        list.Add(new ToolUseBlockParam { ID = u.Id, Name = u.Name, Input = input });
                    }
                    break;
                case AiToolResult tr:
                    list.Add(new ToolResultBlockParam { ToolUseID = tr.ToolUseId, Content = tr.Content, IsError = tr.IsError });
                    break;
            }
        }
        return new MessageParam { Role = m.Role == AiRole.User ? Role.User : Role.Assistant, Content = list };
    }
}
