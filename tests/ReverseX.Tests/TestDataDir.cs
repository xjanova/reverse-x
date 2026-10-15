using System.Runtime.CompilerServices;

namespace ReverseX.Tests;

/// <summary>Points AppPaths at a throw-away folder so tests never read or write the user's real %LOCALAPPDATA%\ReverseX.</summary>
internal static class TestDataDir
{
    public static string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rx-data-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("REVERSEX_DATA_DIR", Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }
}
