using ReverseX.Core.Files;

namespace ReverseX.App.ViewModels;

/// <summary>Maps a detected file kind to its 3D icon, display-name key and stat group.</summary>
public static class KindInfo
{
    private const string Icons = "/ReverseX;component/Assets/Icons3D/";

    public static string Icon(FileKind kind) => Icons + kind switch
    {
        FileKind.Pe or FileKind.Elf or FileKind.MachO or FileKind.Wasm => "binary.png",
        FileKind.DotNet => "dotnet.png",
        FileKind.Apk => "android.png",
        FileKind.Asar or FileKind.AppFolder => "javascript.png",
        FileKind.Sqlite => "database.png",
        FileKind.Har => "web.png",
        _ => "unknown.png",
    };

    public static string NameKey(FileKind kind) => "Kind." + kind;

    public static string Group(FileKind kind) => kind switch
    {
        FileKind.Pe or FileKind.DotNet or FileKind.Elf or FileKind.MachO or FileKind.Wasm => "binary",
        FileKind.Asar or FileKind.AppFolder => "js",
        FileKind.Apk => "apk",
        _ => "other",
    };
}
