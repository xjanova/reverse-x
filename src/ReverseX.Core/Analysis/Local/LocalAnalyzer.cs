using ReverseX.Core.Files;

namespace ReverseX.Core.Analysis.Local;

/// <summary>
/// Runs the in-process readers (no REA, no Node, no Java) and wraps the result in a snapshot.
/// Reader failures on malformed input become AnalysisFailure so the UI shows a friendly sentence.
/// </summary>
public static class LocalAnalyzer
{
    /// <summary>The analysis kind a page should use for this file, or null when no local reader handles it.</summary>
    public static AnalysisKind? KindFor(FileKind kind) => kind switch
    {
        FileKind.Apk => AnalysisKind.Android,
        FileKind.Sqlite => AnalysisKind.Db,
        FileKind.Har or FileKind.Wasm => AnalysisKind.Web,
        FileKind.DotNet => AnalysisKind.DotNetMeta,
        _ => null,
    };

    public static Task<AnalysisSnapshot> AnalyzeAsync(string path, AnalysisKind kind, IProgress<AnalysisProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Analyze(path, kind, progress, ct), ct);

    public static AnalysisSnapshot Analyze(string path, AnalysisKind kind, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var (size, write) = SnapshotStore.Stamp(path);
        var snap = new AnalysisSnapshot
        {
            SourcePath = Path.GetFullPath(path),
            SourceSize = size,
            SourceWriteUtc = write,
            CreatedUtc = DateTime.UtcNow,
            ReaVersion = "local",
            Kind = kind,
        };
        try
        {
            switch (kind)
            {
                case AnalysisKind.Android:
                    return snap with { Android = ApkReader.Read(path, progress, ct) };
                case AnalysisKind.Db:
                    return snap with { Db = SqliteReader.Read(path, progress, ct) };
                case AnalysisKind.DotNetMeta:
                    return snap with { NetMeta = DotNetMetaReader.Read(path, progress, ct) };
                case AnalysisKind.Web:
                    var fk = FileKindDetector.Detect(path).Kind;
                    return fk switch
                    {
                        FileKind.Wasm => snap with { Wasm = WasmReader.Read(path, progress, ct) },
                        FileKind.Har => snap with { Har = HarReader.Read(path, progress, ct) },
                        _ => throw new AnalysisFailure(AnalysisFailureKind.NotSupported, $"web page cannot read {fk}"),
                    };
                default:
                    throw new AnalysisFailure(AnalysisFailureKind.NotSupported, $"no local reader for {kind}");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or BadImageFormatException or System.Text.Json.JsonException
                                       or Microsoft.Data.Sqlite.SqliteException or EndOfStreamException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new AnalysisFailure(AnalysisFailureKind.InputRejected, ex.Message);
        }
    }
}
