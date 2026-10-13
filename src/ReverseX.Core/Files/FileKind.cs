namespace ReverseX.Core.Files;

public enum FileKind
{
    Unknown,
    Folder,
    AppFolder,
    Pe,
    DotNet,
    Elf,
    MachO,
    Apk,
    Asar,
    Sqlite,
    Har,
    Wasm,
}

/// <summary>Result of the quick type check. Detail is a short language-neutral note such as "PE32 x64 · DLL".</summary>
public sealed record FileKindResult(FileKind Kind, string? Detail = null);
