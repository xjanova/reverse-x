using ReverseX.Core.Analysis;

namespace ReverseX.Core.Ai;

public enum AiRole { User, Assistant }

/// <summary>SDK-neutral conversation blocks, so the tool loop can be tested without the network.</summary>
public abstract record AiBlock;
public sealed record AiText(string Text) : AiBlock;
public sealed record AiThinking(string Thinking, string Signature) : AiBlock;
public sealed record AiRedactedThinking(string Data) : AiBlock;
public sealed record AiToolUse(string Id, string Name, string InputJson) : AiBlock;
public sealed record AiToolResult(string ToolUseId, string Content, bool IsError) : AiBlock;

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiBlock> Blocks)
{
    public static AiMessage User(string text) => new(AiRole.User, new AiBlock[] { new AiText(text) });
}

public sealed record AiToolDef(string Name, string Description, string InputSchemaJson);

public sealed record TurnRequest(string Model, string System, IReadOnlyList<AiToolDef> Tools, IReadOnlyList<AiMessage> Messages, int MaxTokens);

public enum AiStop { EndTurn, MaxTokens, ToolUse, PauseTurn, Refusal, ContextExceeded, Other }

public sealed record TurnResult(AiMessage Message, AiStop Stop, AiUsage Usage, string? RefusalCategory = null, string? RefusalExplanation = null);

/// <summary>One streamed model call. The real one is <see cref="AnthropicTurnStreamer"/>; tests use a scripted fake.</summary>
public interface IModelStreamer
{
    Task<TurnResult> StreamTurnAsync(TurnRequest request, Action<string> onText, Action<string> onThinking, CancellationToken ct);
}

public enum FindingKind { Path, Evidence, Unknown }

/// <summary>One thing the model reported with record_finding. Location is file:line or an address.</summary>
public sealed record Finding(FindingKind Kind, EvidenceLevel Level, string Text, string? Location, IReadOnlyList<string> Steps);

/// <summary>A code change the model proposed for the user's own project. Nothing is written until the user confirms.</summary>
public sealed record FileProposal(string RelativePath, string? OldContent, string NewContent, string Diff, string Explanation)
{
    public bool IsNewFile => OldContent is null;
}

public enum AgentOutcome { Done, Refused, Cancelled, BudgetExceeded, StepLimit, Truncated, Failed }

public abstract record AgentEvent;
public sealed record StepStarted(string Id, string Tool, string Title, string Detail) : AgentEvent;
public sealed record StepFinished(string Id, bool Ok, TimeSpan Elapsed, string Summary) : AgentEvent;
public sealed record TextDelta(string Text) : AgentEvent;
public sealed record ThinkingDelta(string Text) : AgentEvent;
public sealed record FindingRecorded(Finding Finding) : AgentEvent;
public sealed record ProposalReady(FileProposal Proposal) : AgentEvent;
public sealed record CostUpdated(decimal CostUsd, AiUsage Usage) : AgentEvent;
public sealed record TurnStarted(int Turn) : AgentEvent;
public sealed record Finished(AgentOutcome Outcome, string? Message) : AgentEvent;

public sealed record AgentResult(AgentOutcome Outcome, string? Message, IReadOnlyList<AiMessage> History, decimal CostUsd, AiUsage Usage);
