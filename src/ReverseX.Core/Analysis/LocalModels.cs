namespace ReverseX.Core.Analysis;

// Models for the analyses Reverse X does itself (no REA process): APK, SQLite, HAR, WASM and .NET metadata.
// Texts here are data (names, values); the UI localises labels and maps Insight codes to sentences.

public enum InsightTone { Info, Good, Warn, Risk }

/// <summary>A notable fact the reader found. Code maps to "Ins.{Code}" in the string tables; Args fill its placeholders.</summary>
public sealed record Insight(string Code, InsightTone Tone, EvidenceLevel Level, List<string> Args)
{
    public static Insight Of(string code, InsightTone tone, EvidenceLevel level, params object[] args) =>
        new(code, tone, level, args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList());
}

/// <summary>A string that looks like a credential. Value is already masked.</summary>
public sealed record SecretHit(string Kind, string Masked, string Where);

// ───────────────────────── Android ─────────────────────────

public sealed record AndroidPermission(string Name, string Protection);

public sealed record AndroidComponent
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Exported { get; init; }
    public string? Permission { get; init; }
    public List<string> Actions { get; init; } = new();
    public List<string> Categories { get; init; } = new();
    public List<string> Data { get; init; } = new();
}

public sealed record AndroidNativeLib(string Abi, string Name, long Size);

public sealed record AndroidSdk(string Name, string Category, int Classes);

public sealed record AndroidCert
{
    public string Scheme { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public string Algorithm { get; init; } = "";
    public string KeyInfo { get; init; } = "";
}

public sealed record NamedCount(string Name, long Count);

public sealed record AndroidModel
{
    public string PackageName { get; init; } = "";
    public string? Label { get; init; }
    public string? VersionName { get; init; }
    public string? VersionCode { get; init; }
    public int? MinSdk { get; init; }
    public int? TargetSdk { get; init; }
    public int? CompileSdk { get; init; }
    public string? AppClass { get; init; }
    public bool Debuggable { get; init; }
    public bool? AllowBackup { get; init; }
    public bool? CleartextTraffic { get; init; }
    public bool NetworkSecurityConfig { get; init; }
    public string Framework { get; init; } = "";
    public List<AndroidPermission> Permissions { get; init; } = new();
    public List<string> DeclaredPermissions { get; init; } = new();
    public List<AndroidComponent> Components { get; init; } = new();
    public List<string> DexFiles { get; init; } = new();
    public int ClassCount { get; init; }
    public List<string> Classes { get; init; } = new();
    public List<NamedCount> Packages { get; init; } = new();
    public List<AndroidSdk> Sdks { get; init; } = new();
    public List<string> Abis { get; init; } = new();
    public List<AndroidNativeLib> NativeLibs { get; init; } = new();
    public List<AndroidCert> Certificates { get; init; } = new();
    public List<string> Schemes { get; init; } = new();
    public List<string> Urls { get; init; } = new();
    public List<SecretHit> Secrets { get; init; } = new();
    public List<NamedCount> TopFolders { get; init; } = new();
    public int FileCount { get; init; }
    public long UncompressedSize { get; init; }
    public List<string> Splits { get; init; } = new();
    public string ManifestXml { get; init; } = "";
    public List<Insight> Insights { get; init; } = new();
}

// ───────────────────────── SQLite ─────────────────────────

public sealed record DbColumn(string Name, string Type, bool NotNull, bool PrimaryKey, string? Default);

public sealed record DbTable
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "table";
    public long? RowCount { get; init; }
    public string Sql { get; init; } = "";
    public List<DbColumn> Columns { get; init; } = new();
    public List<string> Indexes { get; init; } = new();
    public List<string> ForeignKeys { get; init; } = new();
    public List<string> SampleColumns { get; init; } = new();
    public List<List<string?>> Sample { get; init; } = new();
    public List<string> SensitiveColumns { get; init; } = new();
}

public sealed record DbModel
{
    public int PageSize { get; init; }
    public long PageCount { get; init; }
    public long FreelistPages { get; init; }
    public string TextEncoding { get; init; } = "";
    public int UserVersion { get; init; }
    public uint ApplicationId { get; init; }
    public bool WalMode { get; init; }
    public long WalBytes { get; init; }
    public bool WalIncluded { get; init; }
    public string WriterVersion { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public List<string> Origins { get; init; } = new();
    public List<DbTable> Tables { get; init; } = new();
    public List<NamedCount> Triggers { get; init; } = new();
    public List<string> TriggerSql { get; init; } = new();
    public List<Insight> Insights { get; init; } = new();
}

// ───────────────────────── Web: HAR + WASM ─────────────────────────

public sealed record NameValue(string Name, string Value);

public sealed record HarEntry
{
    public int Index { get; init; }
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public string Host { get; init; } = "";
    public string Path { get; init; } = "";
    public string Template { get; init; } = "";
    public int Status { get; init; }
    public string? StatusText { get; init; }
    public string? MimeType { get; init; }
    public long ResponseBytes { get; init; }
    public long RequestBytes { get; init; }
    public double TimeMs { get; init; }
    public double StartMs { get; init; }
    public string? ServerIp { get; init; }
    public string? HttpVersion { get; init; }
    public List<NameValue> RequestHeaders { get; init; } = new();
    public List<NameValue> ResponseHeaders { get; init; } = new();
    public List<NameValue> Query { get; init; } = new();
    public string? RequestBody { get; init; }
    public string? ResponseBody { get; init; }
    public bool HasCredentials { get; init; }
}

public sealed record HarHost(string Host, int Requests, long Bytes, bool Tls, int Errors, string? Tracker);

public sealed record HarEndpoint(string Method, string Host, string Template, int Count, int Errors, double AvgMs, string? MimeType);

public sealed record HarModel
{
    public string Creator { get; init; } = "";
    public string? Browser { get; init; }
    public int Pages { get; init; }
    public int TotalEntries { get; init; }
    public List<HarEntry> Entries { get; init; } = new();
    public List<HarHost> Hosts { get; init; } = new();
    public List<HarEndpoint> Endpoints { get; init; } = new();
    public int Errors4xx { get; init; }
    public int Errors5xx { get; init; }
    public long TotalBytes { get; init; }
    public double SpanMs { get; init; }
    public int RedactedValues { get; init; }
    public List<NamedCount> MimeTypes { get; init; } = new();
    public List<Insight> Insights { get; init; } = new();
}

public sealed record WasmImport(string Module, string Name, string Kind);

public sealed record WasmExport(string Name, string Kind, uint Index);

public sealed record WasmModel
{
    public uint Version { get; init; }
    public int DefinedFunctions { get; init; }
    public int ImportedFunctions { get; init; }
    public int Types { get; init; }
    public int Tables { get; init; }
    public int Globals { get; init; }
    public int DataSegments { get; init; }
    public bool HasStart { get; init; }
    public ulong? MemoryMinPages { get; init; }
    public ulong? MemoryMaxPages { get; init; }
    public bool MemoryShared { get; init; }
    public string Toolchain { get; init; } = "";
    public List<WasmImport> Imports { get; init; } = new();
    public List<WasmExport> Exports { get; init; } = new();
    public List<NamedCount> Sections { get; init; } = new();
    public List<string> Producers { get; init; } = new();
    public List<string> FunctionNames { get; init; } = new();
    public List<string> Strings { get; init; } = new();
    public List<Insight> Insights { get; init; } = new();
}

// ───────────────────────── .NET metadata (read locally) ─────────────────────────

public sealed record NetTypeInfo(string Namespace, string Name, string Kind, bool Public, int Methods, int Fields, int Properties, string? BaseType);

public sealed record NetPInvoke(string Module, string Entry, string Method);

public sealed record NetRef(string Name, string Version, string? PublicKeyToken);

public sealed record NetResource(string Name, long Size, bool Public);

public sealed record NetApiUse(string Category, string Api, int Count);

public sealed record NetMetaModel
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Culture { get; init; }
    public string? PublicKeyToken { get; init; }
    public string? TargetFramework { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Title { get; init; }
    public string? Copyright { get; init; }
    public string? InformationalVersion { get; init; }
    public string Mvid { get; init; } = "";
    public string MetadataVersion { get; init; } = "";
    public string Machine { get; init; } = "";
    public bool IsDll { get; init; }
    public bool ILOnly { get; init; }
    public bool ReadyToRun { get; init; }
    public bool StrongNameSigned { get; init; }
    public string? EntryPoint { get; init; }
    public string? Obfuscator { get; init; }
    public int UnreadableNames { get; init; }
    public int UserStringCount { get; init; }
    public List<string> InternalsVisibleTo { get; init; } = new();
    public List<NetTypeInfo> Types { get; init; } = new();
    public List<NetRef> References { get; init; } = new();
    public List<NetPInvoke> PInvokes { get; init; } = new();
    public List<NetResource> Resources { get; init; } = new();
    public List<NetApiUse> ApiUse { get; init; } = new();
    public List<string> Urls { get; init; } = new();
    public List<SecretHit> Secrets { get; init; } = new();
    public List<Insight> Insights { get; init; } = new();
    public int MethodCount => Types.Sum(t => t.Methods);
}
