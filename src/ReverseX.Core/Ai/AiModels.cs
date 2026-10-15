namespace ReverseX.Core.Ai;

/// <summary>USD per million tokens, from Anthropic's published price list (platform.claude.com/docs/en/about-claude/pricing).</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheWrite5m, decimal CacheWrite1h, decimal CacheRead);

/// <summary>One selectable model. LongPrice applies when a single request's prompt exceeds LongThreshold tokens (Haiku 5.5 only).</summary>
public sealed record ClaudeModel(string Id, string Display, ModelPrice Price, ModelPrice? LongPrice = null, long LongThreshold = 100_000);

public static class AiModels
{
    public const string DefaultId = "claude-opus-5-5";

    public static readonly IReadOnlyList<ClaudeModel> All = new[]
    {
        new ClaudeModel("claude-opus-5-5", "Claude Opus 5.5", new ModelPrice(4m, 20m, 5m, 8m, 0.20m)),
        new ClaudeModel("claude-sonnet-5-5", "Claude Sonnet 5.5", new ModelPrice(2m, 10m, 2.5m, 4m, 0.10m)),
        new ClaudeModel("claude-haiku-5-5", "Claude Haiku 5.5", new ModelPrice(0.10m, 0.50m, 0.125m, 0.20m, 0.01m),
            new ModelPrice(0.50m, 2.50m, 0.625m, 1m, 0.05m)),
        new ClaudeModel("claude-fable-5-1", "Claude Fable 5.1", new ModelPrice(10m, 50m, 12.5m, 20m, 0.25m)),
    };

    public static ClaudeModel Get(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? All[0];

    public static ClaudeModel? Find(string? id) => All.FirstOrDefault(m => m.Id == id);

    public static bool IsKnown(string? id) => All.Any(m => m.Id == id);
}

/// <summary>Token counts of one API response. Input excludes cached tokens (the API reports them separately).</summary>
public sealed record AiUsage(long Input, long Output, long CacheWrite5m = 0, long CacheWrite1h = 0, long CacheRead = 0)
{
    public static readonly AiUsage Zero = new(0, 0);

    public AiUsage Plus(AiUsage o) => new(Input + o.Input, Output + o.Output, CacheWrite5m + o.CacheWrite5m,
        CacheWrite1h + o.CacheWrite1h, CacheRead + o.CacheRead);

    public long PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;
}

public static class CostCalculator
{
    /// <summary>Cost of ONE request in USD: tokens x the model's list price (long-prompt tier chosen per request).</summary>
    public static decimal Cost(ClaudeModel model, AiUsage u)
    {
        var p = model.LongPrice is not null && u.PromptTokens > model.LongThreshold ? model.LongPrice : model.Price;
        return (u.Input * p.Input + u.Output * p.Output + u.CacheWrite5m * p.CacheWrite5m
                + u.CacheWrite1h * p.CacheWrite1h + u.CacheRead * p.CacheRead) / 1_000_000m;
    }

    /// <summary>"$0.0123" style text; very small amounts keep 4 decimals so they never show as $0.00.</summary>
    public static string Format(decimal usd) =>
        usd >= 1m ? usd.ToString("$0.00", System.Globalization.CultureInfo.InvariantCulture)
                  : usd.ToString("$0.0000", System.Globalization.CultureInfo.InvariantCulture);
}
