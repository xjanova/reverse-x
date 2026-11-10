using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReverseX.Core.Engines;

/// <summary>Where a resolved engine came from.</summary>
public enum EngineSource { None, UserEnvironment, AppInstalled }

/// <summary>
/// The engines REA should use and the environment variables that select them. <see cref="Variables"/> only ever
/// adds or overrides; anything else the user set is inherited unchanged by the REA process.
/// </summary>
public sealed record EngineEnvironment(
    string? GhidraDir, EngineSource GhidraSource,
    string? JavaHome, EngineSource JavaSource,
    string? JadxMcpJar, EngineSource JadxMcpSource,
    IReadOnlyDictionary<string, string> Variables)
{
    public IReadOnlyDictionary<string, string> Variables { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase));

    public static EngineEnvironment None { get; } = new(null, EngineSource.None, null, EngineSource.None, null, EngineSource.None,
        new Dictionary<string, string>());

    /// <summary>Copies <see cref="Variables"/> into a process environment (e.g. <c>ProcessStartInfo.Environment</c>).</summary>
    public void ApplyTo(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var (name, value) in Variables) environment[name] = value;
    }
}

/// <summary>
/// Picks the Ghidra, JDK and JADX MCP JAR that REA should use. Only a handful of File.Exists checks and two tiny
/// text reads, so it is cheap enough to run before every REA start. Never throws for missing or unreadable files.
/// </summary>
public sealed class EngineLocator
{
    public const string GhidraInstallDirVariable = "GHIDRA_INSTALL_DIR";
    public const string JavaHomeVariable = "JAVA_HOME";
    public const string PathVariable = "PATH";
    public const string JadxMcpJarVariable = "REA_JADX_MCP_JAR";

    private const long MaxSmallFileBytes = 256 * 1024;

    private static readonly Regex GhidraRelease = new(@"^(\d+)\.(\d+)(?:\.(\d+))?([-+].*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex JavaVersion = new(@"^(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

    private readonly string? _root;
    private readonly Func<string, string?> _env;

    /// <param name="root">Data root holding <c>engines\</c>; defaults to <see cref="AppPaths.Root"/>.</param>
    /// <param name="environment">Environment lookup; defaults to this process's environment.</param>
    public EngineLocator(string? root = null, Func<string, string?>? environment = null)
    {
        _root = root;
        _env = environment ?? Environment.GetEnvironmentVariable;
    }

    public string EnginesDir => _root is null ? AppPaths.EnginesDir : Path.Combine(_root, "engines");

    /// <summary>Resolves with the real data folder and process environment; logs and returns <see cref="EngineEnvironment.None"/> on any surprise.</summary>
    public static EngineEnvironment ResolveDefault()
    {
        try { return new EngineLocator().Resolve(); }
        catch (Exception ex)
        {
            AppPaths.Log("Engine lookup failed", ex);
            return EngineEnvironment.None;
        }
    }

    public EngineEnvironment Resolve()
    {
        var engines = EnginesDir;
        var (ghidra, ghidraSource) = ResolveGhidra(engines);
        var (java, javaSource) = ResolveJava(engines);
        var (jar, jarSource) = ResolveJadxMcp(engines);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ghidra is not null) vars[GhidraInstallDirVariable] = ghidra;
        if (java is not null)
        {
            vars[JavaHomeVariable] = java;
            vars[PathVariable] = PrependPath(Path.Combine(java, "bin"), _env(PathVariable));
        }
        if (jar is not null) vars[JadxMcpJarVariable] = jar;
        return new EngineEnvironment(ghidra, ghidraSource, java, javaSource, jar, jarSource, vars);
    }

    // The user's GHIDRA_INSTALL_DIR wins when it is a usable 12.1.x; a wrong version falls back to the app copy.
    private (string?, EngineSource) ResolveGhidra(string engines)
    {
        var user = Clean(_env(GhidraInstallDirVariable));
        if (user is not null && IsValidInstall(EngineCatalog.Ghidra, user)) return (user, EngineSource.UserEnvironment);
        var app = Path.Combine(engines, EngineCatalog.Ghidra.FolderName);
        return IsValidInstall(EngineCatalog.Ghidra, app) ? (app, EngineSource.AppInstalled) : (null, EngineSource.None);
    }

    // The app's JDK is the one verified with Ghidra 12, so it is preferred; the user's JAVA_HOME only if it is 21+.
    private (string?, EngineSource) ResolveJava(string engines)
    {
        var app = Path.Combine(engines, EngineCatalog.Jdk.FolderName);
        if (IsValidInstall(EngineCatalog.Jdk, app)) return (app, EngineSource.AppInstalled);
        var user = Clean(_env(JavaHomeVariable));
        if (user is not null && IsValidInstall(EngineCatalog.Jdk, user) && ReadJavaMajor(user) >= EngineCatalog.GhidraMinJavaMajor)
            return (user, EngineSource.UserEnvironment);
        return (null, EngineSource.None);
    }

    private (string?, EngineSource) ResolveJadxMcp(string engines)
    {
        var user = Clean(_env(JadxMcpJarVariable));
        if (user is not null && Path.IsPathFullyQualified(user) && File.Exists(user)) return (user, EngineSource.UserEnvironment);
        var folder = Path.Combine(engines, EngineCatalog.JadxMcp.FolderName);
        return IsValidInstall(EngineCatalog.JadxMcp, folder)
            ? (Path.Combine(folder, EngineCatalog.JadxMcp.ProbeFile), EngineSource.AppInstalled)
            : (null, EngineSource.None);
    }

    /// <summary>
    /// True when <paramref name="dir"/> is an absolute folder holding <paramref name="spec"/>'s probe file; for Ghidra the
    /// folder must also declare a 12.1.x <c>application.version</c> (REA refuses other release lines).
    /// </summary>
    public static bool IsValidInstall(EngineSpec spec, string dir)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) return false;
        if (!File.Exists(Path.Combine(dir, spec.ProbeFile))) return false;
        return spec.Kind != EngineKind.Ghidra || IsSupportedGhidraVersion(ReadGhidraVersion(dir));
    }

    /// <summary><c>application.version</c> from <c>Ghidra\application.properties</c>, or null.</summary>
    public static string? ReadGhidraVersion(string ghidraDir) =>
        ReadProperty(Path.Combine(ghidraDir, "Ghidra", "application.properties"), "application.version");

    public static bool IsSupportedGhidraVersion(string? version)
    {
        if (version is null) return false;
        var m = GhidraRelease.Match(version.Trim());
        return m.Success
               && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
               && major == EngineCatalog.GhidraMajor && minor == EngineCatalog.GhidraMinor;
    }

    /// <summary>Major version from a JDK's <c>release</c> file (<c>JAVA_VERSION="21.0.13"</c> → 21), or null.</summary>
    public static int? ReadJavaMajor(string javaHome) =>
        ParseJavaMajor(ReadProperty(Path.Combine(javaHome, "release"), "JAVA_VERSION"));

    /// <summary>"21.0.13" → 21, "17" → 17, "1.8.0_392" → 8, "25-ea" → 25.</summary>
    public static int? ParseJavaMajor(string? version)
    {
        if (version is null) return null;
        var m = JavaVersion.Match(version.Trim().Trim('"'));
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return null;
        if (major == 1 && m.Groups[2].Success
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var legacy)) return legacy;
        return major;
    }

    /// <summary>Puts <paramref name="dir"/> first on a PATH value unless it already is first.</summary>
    public static string PrependPath(string dir, string? existing)
    {
        if (string.IsNullOrEmpty(existing)) return dir;
        var first = existing.Split(Path.PathSeparator, 2)[0].Trim().Trim('"');
        return string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase)
            ? existing
            : dir + Path.PathSeparator + existing;
    }

    private static string? Clean(string? value)
    {
        var v = value?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Reads <c>key=value</c> from a small properties-style file; null if missing, unreadable or oversized.</summary>
    private static string? ReadProperty(string file, string key)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxSmallFileBytes) return null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0 || !string.Equals(line[..eq].Trim(), key, StringComparison.Ordinal)) continue;
                var value = line[(eq + 1)..].Trim().Trim('"').Trim();
                return value.Length == 0 ? null : value;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
