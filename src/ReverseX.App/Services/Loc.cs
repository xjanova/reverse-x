using System.Globalization;
using System.Windows;
using ReverseX.Core;

namespace ReverseX.App.Services;

/// <summary>Thai / English UI text from Resources/Strings.*.xaml. Default is Thai.</summary>
public static class Loc
{
    public static string Current { get; private set; } = UiPrefs.Thai;
    public static event Action? Changed;

    public static void Apply(string lang, bool save = true)
    {
        lang = lang == UiPrefs.English ? UiPrefs.English : UiPrefs.Thai;
        ThemeManager.Swap("Strings.", new Uri($"pack://application:,,,/ReverseX;component/Resources/Strings.{lang}.xaml"));
        Current = lang;
        if (save)
        {
            var prefs = UiPrefs.Load();
            prefs.Language = lang;
            prefs.Save();
        }
        Changed?.Invoke();
    }

    public static string Get(string key)
    {
        var app = Application.Current;
        return app?.TryFindResource("S." + key) as string ?? key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
