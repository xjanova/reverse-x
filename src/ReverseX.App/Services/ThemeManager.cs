using System.Windows;
using ReverseX.Core;

namespace ReverseX.App.Services;

/// <summary>
/// Night / sky look. Preference lives in %LOCALAPPDATA%\ReverseX\ui.json. Switching replaces the palette dictionary
/// in Application.Resources.MergedDictionaries; every style uses DynamicResource, so it applies at once.
/// </summary>
public static class ThemeManager
{
    public static string Current { get; private set; } = UiPrefs.Night;
    public static event Action? Changed;

    public static void Apply(string theme, bool save = true)
    {
        theme = theme == UiPrefs.Sky ? UiPrefs.Sky : UiPrefs.Night;
        Swap("Palette.", new Uri($"pack://application:,,,/ReverseX;component/Theme/Palette.{(theme == UiPrefs.Sky ? "Sky" : "Night")}.xaml"));
        Current = theme;
        if (save)
        {
            var prefs = UiPrefs.Load();
            prefs.Theme = theme;
            prefs.Save();
        }
        Changed?.Invoke();
    }

    internal static void Swap(string marker, Uri source)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var fresh = new ResourceDictionary { Source = source };
        var at = -1;
        for (var i = 0; i < merged.Count; i++)
            if (merged[i].Source?.OriginalString.Contains(marker, StringComparison.Ordinal) == true) at = i;
        if (at >= 0) merged[at] = fresh;
        else merged.Insert(0, fresh);
    }
}
