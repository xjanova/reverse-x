namespace ReverseX.Core.Ai;

public enum AiProviderKind { Anthropic, OpenAiCompatible }

/// <summary>One model vendor. Everything except Anthropic speaks the OpenAI "chat/completions" dialect, which also covers local servers and gateways.</summary>
public sealed record AiProvider(string Id, string Display, AiProviderKind Kind, string BaseUrl, string DefaultModel, bool NeedsKey, bool UsesMaxCompletionTokens = false)
{
    public bool IsAnthropic => Kind == AiProviderKind.Anthropic;
    public bool UrlEditable => Id is "custom" or "ollama" or "lmstudio";
}

public static class AiProviders
{
    public const string DefaultId = "anthropic";

    public static readonly IReadOnlyList<AiProvider> All = new[]
    {
        new AiProvider("anthropic", "Anthropic (Claude)", AiProviderKind.Anthropic, "https://api.anthropic.com", AiModels.DefaultId, true),
        new AiProvider("openai", "OpenAI (GPT)", AiProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "gpt-4.1", true, UsesMaxCompletionTokens: true),
        new AiProvider("gemini", "Google Gemini", AiProviderKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-pro", true),
        new AiProvider("openrouter", "OpenRouter (หลายค่าย)", AiProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4.5", true),
        new AiProvider("deepseek", "DeepSeek", AiProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1", "deepseek-chat", true),
        new AiProvider("groq", "Groq", AiProviderKind.OpenAiCompatible, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true),
        new AiProvider("mistral", "Mistral", AiProviderKind.OpenAiCompatible, "https://api.mistral.ai/v1", "mistral-large-latest", true),
        new AiProvider("xai", "xAI (Grok)", AiProviderKind.OpenAiCompatible, "https://api.x.ai/v1", "grok-4", true),
        new AiProvider("together", "Together AI", AiProviderKind.OpenAiCompatible, "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", true),
        new AiProvider("ollama", "Ollama (ในเครื่อง)", AiProviderKind.OpenAiCompatible, "http://localhost:11434/v1", "qwen2.5-coder", false),
        new AiProvider("lmstudio", "LM Studio (ในเครื่อง)", AiProviderKind.OpenAiCompatible, "http://localhost:1234/v1", "local-model", false),
        new AiProvider("custom", "กำหนดเอง (OpenAI-compatible URL / gateway)", AiProviderKind.OpenAiCompatible, "", "", true),
    };

    public static AiProvider Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    public static bool IsKnown(string? id) => All.Any(p => p.Id == id);

    /// <summary>The model id sent to the API for the current preferences.</summary>
    public static string ModelId(AiPrefs prefs) =>
        Get(prefs.Provider).IsAnthropic ? prefs.Model : (string.IsNullOrWhiteSpace(prefs.CustomModel) ? Get(prefs.Provider).DefaultModel : prefs.CustomModel.Trim());

    public static string BaseUrl(AiPrefs prefs)
    {
        var p = Get(prefs.Provider);
        return p.UrlEditable && !string.IsNullOrWhiteSpace(prefs.CustomBaseUrl) ? prefs.CustomBaseUrl.Trim() : p.BaseUrl;
    }

    /// <summary>https anywhere, plain http only for this machine / a private network (the key travels in the request).</summary>
    public static bool IsAcceptableUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        if (u.Scheme != Uri.UriSchemeHttp) return false;
        if (u.IsLoopback) return true;
        if (!System.Net.IPAddress.TryParse(u.Host, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Only Claude has a built-in price list; other vendors run without a cost estimate (shown as $0).</summary>
    public static ClaudeModel PricingFor(string modelId) =>
        AiModels.Find(modelId) ?? new ClaudeModel(modelId, modelId, new ModelPrice(0, 0, 0, 0, 0));
}
