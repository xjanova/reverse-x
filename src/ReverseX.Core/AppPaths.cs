namespace ReverseX.Core;

/// <summary>Per-user data folder (%LOCALAPPDATA%\ReverseX). REVERSEX_DATA_DIR overrides it (tests, screenshots).</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("REVERSEX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReverseX");
        }
    }

    public static string UiJson => Path.Combine(Root, "ui.json");
    public static string ProjectsJson => Path.Combine(Root, "projects.json");
    public static string DownloadsDir => Path.Combine(Root, "downloads");
    public static string LogFile => Path.Combine(Root, "logs", "app.log");

    /// <summary>Best-effort diagnostics log; raw exceptions go here, never on screen.</summary>
    public static void Log(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:u} {message}{(ex is null ? "" : " :: " + ex)}{Environment.NewLine}");
        }
        catch (Exception) { /* logging must never break the app */ }
    }
}
