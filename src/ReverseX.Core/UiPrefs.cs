using System.Text.Json;

namespace ReverseX.Core;

/// <summary>Theme + language preference, kept in ui.json. A missing or corrupt file means defaults.</summary>
public sealed class UiPrefs
{
    public const string Night = "night";
    public const string Sky = "sky";
    public const string Thai = "th";
    public const string English = "en";

    public string Theme { get; set; } = Night;
    public string Language { get; set; } = Thai;

    public static UiPrefs Load(string? path = null)
    {
        path ??= AppPaths.UiJson;
        try
        {
            if (!File.Exists(path)) return new UiPrefs();
            var p = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(path)) ?? new UiPrefs();
            p.Theme = p.Theme == Sky ? Sky : Night;
            p.Language = p.Language == English ? English : Thai;
            return p;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UiPrefs();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.UiJson;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not save ui.json", ex); // the choice just lasts until the app closes
        }
    }
}
