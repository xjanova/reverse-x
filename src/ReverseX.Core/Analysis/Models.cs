namespace ReverseX.Core.Analysis;

/// <summary>How sure a statement is: seen directly by REA, inferred/derived, or not known (limits REA reports).</summary>
public enum EvidenceLevel
{
    Seen,
    Inferred,
    Unknown,
}

public static class EvidenceLevels
{
    /// <summary>REA confidence values are observed | derived | inferred.</summary>
    public static EvidenceLevel FromRea(string? confidence) => confidence switch
    {
        "observed" => EvidenceLevel.Seen,
        "derived" or "inferred" => EvidenceLevel.Inferred,
        _ => EvidenceLevel.Unknown,
    };
}

// ───────────────────────── .NET ─────────────────────────

public sealed record DotNetAssemblyRef(string Name, string Version);

public sealed record DotNetAssemblyInfo
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? TargetFramework { get; init; }
    public string? RuntimeFamily { get; init; }
    public string? Architecture { get; init; }
    public string Sha256 { get; init; } = "";
    public long ByteLength { get; init; }
    public string? Mvid { get; init; }
    public string? MetadataVersion { get; init; }
    public string? CoverageState { get; init; }
    public string Confidence { get; init; } = "observed";
    public Dictionary<string, int> TableRowCounts { get; init; } = new();
    public List<DotNetAssemblyRef> References { get; init; } = new();
    public int ResourceCount { get; init; }
    public int AttributeCount { get; init; }
    public List<string> Limitations { get; init; } = new();
}

/// <summary>One CIL anchor REA decoded (calls, branches, strings, fields…). Operand is resolved to a name when possible.</summary>
public sealed record DotNetAnchor(int IlOffset, string Opcode, string OperandKind, string Operand);

public sealed record DotNetCall(string Target, string Kind, string? TargetToken, string Opcode);

public sealed record DotNetMethod
{
    public string Token { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeclaringType { get; init; } = "";
    public string Signature { get; init; } = "";
    public int Flags { get; init; }
    public bool IsStatic => (Flags & 0x10) != 0;
    public bool IsPublic => (Flags & 0x7) == 6;
    public bool HasBody { get; init; }
    public int? IlSize { get; init; }
    public int? InstructionCount { get; init; }
    public int? MaxStack { get; init; }
    public string? IlSha256 { get; init; }
    public Dictionary<string, int> OpcodeCounts { get; init; } = new();
    public List<DotNetAnchor> Anchors { get; init; } = new();
    public List<DotNetCall> Calls { get; init; } = new();
    public List<string> FieldAccesses { get; init; } = new();
}

public sealed record DotNetType
{
    public string Token { get; init; } = "";
    public string Namespace { get; init; } = "";
    public string Name { get; init; } = "";
    public string FullName { get; init; } = "";
    public int FieldCount { get; init; }
    public List<DotNetMethod> Methods { get; init; } = new();
}

public sealed record DotNetModel
{
    public DotNetAssemblyInfo Info { get; init; } = new();
    public List<DotNetType> Types { get; init; } = new();
    public int MemberRefCount { get; init; }
    public string? MembersCoverage { get; init; }
    public List<string> MembersLimitations { get; init; } = new();
    /// <summary>The list was read from metadata in-process because REA could not list the members (no CIL anchors then).</summary>
    public bool BuiltLocally { get; init; }
    public int MethodCount => Types.Sum(t => t.Methods.Count);
}

// ───────────────────────── JS / Electron ─────────────────────────

public sealed record JsModuleItem(string NodeId, string Kind, string Path);

public sealed record JsAppSummary
{
    public string InputPath { get; init; } = "";
    public string Format { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Confidence { get; init; } = "observed";
    public int RelevantFiles { get; init; }
    public int ParsedFiles { get; init; }
    public int Findings { get; init; }
    public int ParseFailures { get; init; }
    public int BrowserWindows { get; init; }
    public int PreloadEntrypoints { get; init; }
    public int ContextBridgeApis { get; init; }
    public int ExposedApiMembers { get; init; }
    public int IpcOperations { get; init; }
    public int IpcLiteralChannels { get; init; }
    public int IpcDynamicOperations { get; init; }
    public int RendererTransmissions { get; init; }
    public int RendererListeners { get; init; }
    public int MainHandlers { get; init; }
    public int PairedTransmissions { get; init; }
    public int UnpairedLiteralTransmissions { get; init; }
    public int NativeAddonBindings { get; init; }
    public int ResolvedNativeAddons { get; init; }
    public int UtilityProcesses { get; init; }
    public string? ApplicationCoverage { get; init; }
    public string? SemanticCoverage { get; init; }
    public int SemanticUnknowns { get; init; }
    public string? ParentEvidenceId { get; init; }
    public bool IntegrityMismatch { get; init; }
    public List<string> Limitations { get; init; } = new();
    public List<string> Unknowns { get; init; } = new();
}

public sealed record JsAppModel
{
    public JsAppSummary Summary { get; init; } = new();
    public List<JsModuleItem> Modules { get; init; } = new();
    /// <summary>Total modules REA reports; may be larger than Modules when the page cap stopped early.</summary>
    public int ModulesTotal { get; init; }
    public bool ModulesComplete => Modules.Count >= ModulesTotal;
}

// ───────────────────────── native / Go ─────────────────────────

public sealed record NativeIdentity(string Path, string Format, string Kind, string Sha256, string? Architecture);

public sealed record EngineRejection(string ProviderId, string Code, string Reason);

/// <param name="External">An import from another module (Ghidra's EXTERNAL space): listed for reference, there is no code to show.</param>
/// <param name="Thunk">A jump stub to another function.</param>
public sealed record NativeProcedure(string Address, string Name, bool External = false, bool Thunk = false);

public enum NativeCodeView { PseudoCode, Assembly }

/// <param name="Text">The engine's text, or null when it produced none for this function.</param>
/// <param name="Provider">e.g. "Ghidra 12.1.4".</param>
public sealed record NativeCode(string? Text, IReadOnlyList<string> Limitations, string Provider);

/// <summary>
/// Cross-references of one native function as the engine resolved them: entry addresses of the functions that call it and of the
/// functions it calls (imports keep their EXTERNAL: address). Indirect calls the engine could not resolve are not listed.
/// </summary>
public sealed record NativeRefs(IReadOnlyList<string> Callers, IReadOnlyList<string> Callees, IReadOnlyList<string> Limitations, string Provider);

/// <param name="OverloadIndex">JADX's position among same-named methods; stable only within one REA session on one APK.</param>
public sealed record AndroidMethodRef(string Name, string Signature, int OverloadIndex, bool IsConstructor);

public sealed record AndroidField(string Name, string Type);

/// <summary>What JADX reports for one class (members only; bodies come one method at a time).</summary>
public sealed record AndroidClassInfo(string ClassName, IReadOnlyList<AndroidMethodRef> Methods, IReadOnlyList<AndroidField> Fields,
    IReadOnlyList<string> InnerClasses, IReadOnlyList<string> Limitations, string Provider);

/// <param name="Text">Decompiled body, or null when JADX had none.</param>
/// <param name="Representation">"java", or "smali" when JADX could not decompile and fell back.</param>
/// <param name="SourceStatus">"complete", "truncated", … as JADX reports it.</param>
public sealed record AndroidMethodCode(string? Text, string Representation, bool FellBack, string SourceStatus, IReadOnlyList<string> Markers);

/// <summary>One class assembled as Java: fields, then each method body in JADX's order.</summary>
/// <param name="MethodsShown">Bodies fetched (at most <see cref="AnalysisRunner.MaxClassMethods"/>).</param>
/// <param name="FellBack">Methods shown as smali because JADX could not decompile them.</param>
/// <param name="NoBody">Methods JADX returned no text for.</param>
public sealed record AndroidClassCode(string ClassName, string Text, int MethodCount, int MethodsShown, int FellBack, int NoBody,
    IReadOnlyList<string> Limitations, string Provider);

public sealed record NativeModel
{
    public NativeIdentity? Identity { get; init; }
    /// <summary>True when REA answered provider_unavailable: no Ghidra/IDA/Hopper is usable on this machine.</summary>
    public bool EngineMissing { get; init; }
    public List<EngineRejection> Rejections { get; init; } = new();
    public List<NativeProcedure> Procedures { get; init; } = new();
    public int ProcedureTotal { get; init; }
}

public sealed record GoModule(string Path, string Version);

public sealed record GoSetting(string Key, string Value);

public sealed record GoBuildInfoModel
{
    public string Version { get; init; } = "";
    public string? Path { get; init; }
    public string? MainModule { get; init; }
    public string? MainVersion { get; init; }
    public List<GoModule> Dependencies { get; init; } = new();
    public List<GoSetting> Settings { get; init; } = new();
}

// ───────────────────────── snapshot ─────────────────────────

public enum AnalysisKind
{
    DotNet,
    Js,
    Native,
    Android,
    Db,
    Web,
    DotNetMeta,
}

public sealed record AnalysisSnapshot
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string SourcePath { get; init; } = "";
    public long SourceSize { get; init; }
    public DateTime SourceWriteUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string ReaVersion { get; init; } = "";
    public AnalysisKind Kind { get; init; }
    public DotNetModel? DotNet { get; init; }
    public JsAppModel? Js { get; init; }
    public NativeModel? Native { get; init; }
    public GoBuildInfoModel? Go { get; init; }
    public AndroidModel? Android { get; init; }
    public DbModel? Db { get; init; }
    public HarModel? Har { get; init; }
    public WasmModel? Wasm { get; init; }
    public NetMetaModel? NetMeta { get; init; }
}
