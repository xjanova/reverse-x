namespace ReverseX.Core.Engines;

/// <summary>What an engine is for REA; decides how an install folder is validated.</summary>
public enum EngineKind { Ghidra, Jdk, JadxMcp }

/// <summary>How the pinned download is laid out: a ZIP with one top folder, or a single file (a JAR).</summary>
public enum EnginePackaging { ZipArchive, SingleFile }

/// <param name="Id">Stable id for logs and the install marker ("ghidra", "jdk", "jadx-mcp").</param>
/// <param name="Size">Exact download size in bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the download.</param>
/// <param name="FolderName">
/// Install folder under <c>engines\</c>. For <see cref="EnginePackaging.ZipArchive"/> this is the ZIP's top folder;
/// for <see cref="EnginePackaging.SingleFile"/> the downloaded file is placed inside it as <paramref name="ProbeFile"/>.
/// </param>
/// <param name="ProbeFile">Path relative to <paramref name="FolderName"/> whose presence proves the install is usable.</param>
public sealed record EngineSpec(
    string Id,
    EngineKind Kind,
    string DisplayName,
    string Version,
    string Url,
    long Size,
    string Sha256,
    string FolderName,
    string ProbeFile,
    EnginePackaging Packaging,
    string License)
{
    /// <summary>File name used for the <c>.part</c> download (last URL segment, or the id if that is unusable).</summary>
    public string DownloadName
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return Id;
            var name = Uri.UnescapeDataString(uri.Segments.Length == 0 ? "" : uri.Segments[^1]);
            return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? Id : name;
        }
    }
}

/// <summary>The engines Reverse X can install for REA. Versions, sizes and hashes are pinned; never "latest".</summary>
public static class EngineCatalog
{
    /// <summary>REA 6.4.0 accepts Ghidra 12.1.x (verified build 12.1.4).</summary>
    public const int GhidraMajor = 12;
    public const int GhidraMinor = 1;

    /// <summary>Ghidra 12 needs JDK 21+.</summary>
    public const int GhidraMinJavaMajor = 21;

    /// <summary>jadx-headless-mcp needs a full JDK 17+ (with jdk.compiler).</summary>
    public const int JadxMinJavaMajor = 17;

    public static EngineSpec Ghidra { get; } = new(
        "ghidra", EngineKind.Ghidra, "Ghidra", "12.1.4",
        "https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip",
        569_649_598,
        "ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db",
        "ghidra_12.1.4_PUBLIC",
        Path.Combine("support", "analyzeHeadless.bat"),
        EnginePackaging.ZipArchive,
        "Apache-2.0");

    public static EngineSpec Jdk { get; } = new(
        "jdk", EngineKind.Jdk, "Eclipse Temurin JDK", "21.0.13+9",
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_windows_hotspot_21.0.13_9.zip",
        205_421_281,
        "a4dbaa05fca4bd89d84584e58c459bd732efebb1722af80ab13c6516832004f9",
        "jdk-21.0.13+9",
        Path.Combine("bin", "java.exe"),
        EnginePackaging.ZipArchive,
        "GPL-2.0-only WITH Classpath-exception-2.0");

    /// <summary>The JAR REA 6.4.0 pins in dist/android/JadxRelease.js (same version and SHA-256).</summary>
    public static EngineSpec JadxMcp { get; } = new(
        "jadx-mcp", EngineKind.JadxMcp, "JADX headless MCP", "0.7.1",
        "https://github.com/1013503897/jadx-headless-mcp/releases/download/v0.7.1/jadx-headless-mcp-0.7.1-all.jar",
        42_619_858,
        "6e5eacf500b64292bfb73c49797c1958f6ee44646e43e868039ae7feb573ff75",
        "jadx-headless-mcp-0.7.1",
        "jadx-headless-mcp-0.7.1-all.jar",
        EnginePackaging.SingleFile,
        "Apache-2.0");

    public static IReadOnlyList<EngineSpec> All { get; } = new[] { Ghidra, Jdk, JadxMcp };

    /// <summary>Catalog engines that <paramref name="env"/> could not resolve from either the user's environment or the app folder.</summary>
    public static IReadOnlyList<EngineSpec> MissingFrom(EngineEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var missing = new List<EngineSpec>();
        if (env.GhidraDir is null) missing.Add(Ghidra);
        if (env.JavaHome is null) missing.Add(Jdk);
        if (env.JadxMcpJar is null) missing.Add(JadxMcp);
        return missing;
    }
}
