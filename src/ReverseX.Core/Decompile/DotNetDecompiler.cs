using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace ReverseX.Core.Decompile;

public enum DecompileFailureKind
{
    /// <summary>The file has no CLI metadata (native, damaged, or not a PE at all).</summary>
    NotManaged,
    /// <summary>The token is not a TypeDef/MethodDef row of this assembly.</summary>
    BadToken,
    /// <summary>The engine threw on this member (obfuscation, invalid IL, a reference it could not survive).</summary>
    Failed,
    /// <summary>The export folder could not be written.</summary>
    Io,
}

/// <summary>A problem the UI turns into a Thai/English sentence — the raw detail only goes to the log.</summary>
public sealed class DecompileFailure : Exception
{
    public DecompileFailureKind Kind { get; }
    public DecompileFailure(DecompileFailureKind kind, string detail, Exception? inner = null) : base(detail, inner) => Kind = kind;
}

/// <summary>
/// C# for one managed assembly through the ILSpy engine (ICSharpCode.Decompiler). It reads metadata and IL only — nothing in the
/// target runs — and the whole image is read into memory up front, so the file is not kept locked.
/// Building the type system is the slow part, so one instance is kept per assembly and reused for every member the user opens.
/// </summary>
public sealed class DotNetDecompiler
{
    /// <summary>Above this the text is cut: a single generated/obfuscated type can run to tens of MB and freeze a TextBox.</summary>
    public const int MaxChars = 2_000_000;

    private readonly PEFile _file;
    private readonly UniversalAssemblyResolver _resolver;
    private readonly DecompilerSettings _settings;
    private readonly CSharpDecompiler _decompiler;
    private readonly object _gate = new();

    public string Path { get; }

    private DotNetDecompiler(string path, PEFile file, UniversalAssemblyResolver resolver, DecompilerSettings settings, CSharpDecompiler decompiler)
    {
        Path = path;
        _file = file;
        _resolver = resolver;
        _settings = settings;
        _decompiler = decompiler;
    }

    public static DotNetDecompiler Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var file = Load(full);
        var resolver = new UniversalAssemblyResolver(full, false, file.DetectTargetFrameworkId());
        var settings = Settings();
        CSharpDecompiler decompiler;
        try { decompiler = new CSharpDecompiler(file, resolver, settings); }
        catch (Exception ex) when (ex is BadImageFormatException or MetadataFileNotSupportedException or InvalidOperationException)
        {
            throw new DecompileFailure(DecompileFailureKind.NotManaged, full + ": " + ex.Message, ex);
        }
        return new DotNetDecompiler(full, file, resolver, settings, decompiler);
    }

    private static PEFile Load(string full)
    {
        try
        {
            using var stream = File.OpenRead(full);
            return new PEFile(full, stream, PEStreamOptions.PrefetchEntireImage);
        }
        catch (Exception ex) when (ex is BadImageFormatException or MetadataFileNotSupportedException or InvalidOperationException)
        {
            throw new DecompileFailure(DecompileFailureKind.NotManaged, full + ": " + ex.Message, ex);
        }
    }

    private static DecompilerSettings Settings() => new(LanguageVersion.Latest)
    {
        // a missing dependency must not stop the decompile: ILSpy then shows the unresolved names as written in metadata
        ThrowOnAssemblyResolveErrors = false,
        ShowXmlDocumentation = false,
    };

    /// <summary>C# for a method ("0x06……") or a whole type ("0x02……") in REA's token format.</summary>
    public string Decompile(string token, CancellationToken ct)
    {
        var handle = ParseToken(token);
        return Run(handle, ct);
    }

    /// <summary>C# for the type that declares the given method token (the whole class around one method).</summary>
    public string DecompileDeclaringType(string methodToken, CancellationToken ct)
    {
        var handle = ParseToken(methodToken);
        if (handle.Kind != HandleKind.MethodDefinition)
            throw new DecompileFailure(DecompileFailureKind.BadToken, "not a method token: " + methodToken);
        var type = _file.Metadata.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType();
        return Run(type, ct);
    }

    private string Run(EntityHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string text;
        // CSharpDecompiler is not thread-safe; the UI cancels the previous request before starting the next anyway.
        lock (_gate)
        {
            _decompiler.CancellationToken = ct;
            try { text = _decompiler.DecompileAsString(handle); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new DecompileFailure(DecompileFailureKind.Failed, $"{Path} {MetadataTokens.GetToken(handle):X8}: {ex.Message}", ex);
            }
            finally { _decompiler.CancellationToken = default; }
        }
        return Cap(text);
    }

    public static string Cap(string text) =>
        text.Length <= MaxChars ? text : text[..MaxChars] + Environment.NewLine + "// … (" + (text.Length - MaxChars).ToString("N0", CultureInfo.InvariantCulture) + " more characters cut)";

    /// <summary>"0x06000001" → handle, checked against this assembly's own tables so a stale or foreign token fails cleanly.</summary>
    private EntityHandle ParseToken(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new DecompileFailure(DecompileFailureKind.BadToken, "bad token: " + token);
        var table = (TableIndex)(value >> 24);
        var row = value & 0xFFFFFF;
        if (table is not (TableIndex.MethodDef or TableIndex.TypeDef) || row == 0 || row > _file.Metadata.GetTableRowCount(table))
            throw new DecompileFailure(DecompileFailureKind.BadToken, "token not in this assembly: " + token);
        return MetadataTokens.EntityHandle(value);
    }

    /// <summary>
    /// Writes the whole assembly as a C# project (.csproj + one file per type + resources) into <paramref name="targetDirectory"/>,
    /// which must not exist yet or be empty — the caller picks a fresh folder, so nothing of the user's is overwritten.
    /// </summary>
    public void ExportProject(string targetDirectory, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(targetDirectory);
        try
        {
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new DecompileFailure(DecompileFailureKind.Io, "export folder is not empty: " + dir);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DecompileFailure(DecompileFailureKind.Io, dir + ": " + ex.Message, ex);
        }

        // Its own decompiler instances and a settings copy: the per-member view (and _gate) stays usable while this runs.
        var project = new WholeProjectDecompiler(_settings.Clone(), _resolver, null, null, null);
        if (progress is not null)
            project.ProgressIndicator = new Progress<DecompilationProgress>(p => progress.Report((p.UnitsCompleted, p.TotalUnits)));
        try
        {
            project.DecompileProject(_file, dir, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DecompileFailure(DecompileFailureKind.Io, dir + ": " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new DecompileFailure(DecompileFailureKind.Failed, Path + " → " + dir + ": " + ex.Message, ex);
        }
    }

    /// <summary>A folder name next to nothing: "&lt;parent&gt;\&lt;Name&gt;-decompiled", then " (2)", " (3)"… — never an existing one.</summary>
    public static string FreshExportFolder(string parent, string assemblyFileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(assemblyFileName);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        var baseDir = System.IO.Path.Combine(parent, stem + "-decompiled");
        var candidate = baseDir;
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = baseDir + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
        return candidate;
    }
}
