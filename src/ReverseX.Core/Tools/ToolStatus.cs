namespace ReverseX.Core.Tools;

public enum ToolLevel { Checking, Ok, Warn, Bad, Off }

/// <summary>One row of the "tools on this machine" card. MessageCode is a localisation key suffix; the UI turns it into Thai/English text.</summary>
public sealed record ToolStatus(string Id, string DisplayName, ToolLevel Level, string MessageCode, params string[] MessageArgs);

/// <summary>Everything the probes found. Null means "not found / not checked".</summary>
public sealed record ToolProbeInputs(
    Rea.NodeInfo? Node,
    bool NodeProbed,
    Rea.ReaDoctorReport? Doctor,
    Rea.ReaCapabilityReport? Providers,
    string? ReaFailure,          // null = ok, "timeout" or "failed"
    int? JavaMajor,              // the JDK REA will use: the resolved engine JDK if any, else JAVA_HOME / PATH java
    bool JadxFound,
    string? BrowserName,         // "Chrome" / "Edge" / null
    Engines.EngineEnvironment? Engines = null); // what REA is started with; null = not resolved
