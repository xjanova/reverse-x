using System.Text.Json;

namespace ReverseX.Core.Ai;

/// <summary>AI choices (model, per-question budget, last own-project folder). Kept in ai.json. Holds no secrets.</summary>
public sealed class AiPrefs
{
    public const decimal DefaultBudgetUsd = 1.00m;

    public string Provider { get; set; } = AiProviders.DefaultId;
    /// <summary>Claude model id (used when Provider is anthropic).</summary>
    public string Model { get; set; } = AiModels.DefaultId;
    /// <summary>Model id typed by the user for every other vendor.</summary>
    public string? CustomModel { get; set; }
    /// <summary>Endpoint override for custom / local providers.</summary>
    public string? CustomBaseUrl { get; set; }
    public decimal BudgetUsd { get; set; } = DefaultBudgetUsd;
    public string? OwnProjectFolder { get; set; }

    public static string DefaultPath => Path.Combine(AppPaths.Root, "ai.json");

    public static AiPrefs Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AiPrefs();
            var p = JsonSerializer.Deserialize<AiPrefs>(File.ReadAllText(path)) ?? new AiPrefs();
            if (!AiModels.IsKnown(p.Model)) p.Model = AiModels.DefaultId;
            if (!AiProviders.IsKnown(p.Provider)) p.Provider = AiProviders.DefaultId;
            if (p.BudgetUsd <= 0 || p.BudgetUsd > 1000) p.BudgetUsd = DefaultBudgetUsd;
            return p;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AiPrefs();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not save ai.json", ex);
        }
    }
}
