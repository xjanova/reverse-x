using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core.Ai;
using ReverseX.Core.Analysis;

namespace ReverseX.App.ViewModels;

public enum StepState { Running, Ok, Failed }

/// <summary>One REA call in the timeline: Thai title, the telling argument, tool name, state and time.</summary>
public sealed class StepVm : ObservableObject
{
    private StepState _state = StepState.Running;
    private string _elapsed = "";
    private string _summary = "";

    public StepVm(string id, string tool, string title, string detail)
    {
        Id = id; Tool = tool; Title = title; Detail = detail;
        Icon = "/ReverseX;component/Assets/Icons3D/" + IconFor(tool);
    }

    public string Id { get; }
    public string Tool { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Icon { get; }
    public StepState State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(IsRunning)); Raise(nameof(IsFailed)); } } }
    public bool IsRunning => _state == StepState.Running;
    public bool IsFailed => _state == StepState.Failed;
    public string Elapsed { get => _elapsed; set => Set(ref _elapsed, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private static string IconFor(string tool) => tool switch
    {
        _ when tool.Contains("javascript") || tool.Contains("web") || tool.Contains("application_feature") => "javascript.png",
        _ when tool.Contains("managed") => "dotnet.png",
        _ when tool.Contains("android") => "android.png",
        AgentToolbox.RecordFinding => "evidence.png",
        AgentToolbox.ListProjectFiles or AgentToolbox.ReadProjectFile or AgentToolbox.ProposeChange => "function.png",
        AgentToolbox.GetResultPage or "inspect_analysis_view" => "database.png",
        _ when tool.Contains("procedure") || tool.Contains("binary") || tool.Contains("call") || tool.Contains("strings") => "binary.png",
        _ => "ai.png",
    };
}

/// <summary>A chat turn: the user's question, or Reverse X's answer with its steps and notice.</summary>
public sealed class ChatMessageVm : ObservableObject
{
    private string _text = "";
    private string _notice = "";
    private bool _noticeIsError;
    private bool _isRunning;
    private bool _isThinking;
    private string _statusChip = "";

    public bool IsUser { get; init; }
    public bool IsBot => !IsUser;
    public string Text { get => _text; set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool HasText => _text.Length > 0;
    public string Notice { get => _notice; set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => _notice.Length > 0;
    public bool NoticeIsError { get => _noticeIsError; set => Set(ref _noticeIsError, value); }
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }
    public bool IsThinking { get => _isThinking; set => Set(ref _isThinking, value); }
    public string StatusChip { get => _statusChip; set { if (Set(ref _statusChip, value)) Raise(nameof(HasStatusChip)); } }
    public bool HasStatusChip => _statusChip.Length > 0;
    public ObservableCollection<StepVm> Steps { get; } = new();
}

public sealed class FlowNodeVm
{
    public FlowNodeVm(string label, string icon, bool hot) { Label = label; Icon = "/ReverseX;component/Assets/Icons3D/" + icon; IsHot = hot; }
    public string Label { get; }
    public string Icon { get; }
    public bool IsHot { get; }
}

public sealed class EvidenceItemVm
{
    public EvidenceItemVm(Finding f)
    {
        Level = f.Level;
        Badge = Loc.Get(f.Level == EvidenceLevel.Seen ? "Ai.Lvl.Seen" : "Ai.Lvl.Inferred");
        Location = f.Location ?? "";
        Text = f.Text;
    }
    public EvidenceItemVm(EvidenceLevel level, string location, string text)
    {
        Level = level; Badge = Loc.Get(level == EvidenceLevel.Seen ? "Ai.Lvl.Seen" : "Ai.Lvl.Inferred"); Location = location; Text = text;
    }
    public EvidenceLevel Level { get; }
    public bool IsSeen => Level == EvidenceLevel.Seen;
    public string Badge { get; }
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;
    public string Text { get; }
}

public sealed record DiffLineVm(string Text, char Kind);

public enum ProposalState { Pending, Applied, Rejected, Failed }

/// <summary>A proposed file change: diff shown first; the file is written only when the user presses confirm.</summary>
public sealed class ProposalVm : ObservableObject
{
    private readonly string _root;
    private ProposalState _state;
    private string _message = "";

    public ProposalVm(string root, FileProposal p)
    {
        _root = root;
        Proposal = p;
        DiffLines = p.Diff.Replace("\r", "").TrimEnd('\n').Split('\n').Select(l => new DiffLineVm(l, l.StartsWith("--- ") || l.StartsWith("+++ ") ? '@' : l.Length > 0 && "+-@".Contains(l[0]) ? l[0] : ' ')).ToList();
        ApplyCommand = new RelayCommand(Apply, () => _state is ProposalState.Pending or ProposalState.Failed);
        RejectCommand = new RelayCommand(() => State = ProposalState.Rejected, () => _state == ProposalState.Pending);
    }

    public FileProposal Proposal { get; }
    public string Path => Proposal.RelativePath;
    public string Explanation => Proposal.Explanation;
    public bool IsNewFile => Proposal.IsNewFile;
    public IReadOnlyList<DiffLineVm> DiffLines { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RejectCommand { get; }

    public ProposalState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { Raise(nameof(IsPending)); Raise(nameof(IsApplied)); Raise(nameof(IsRejected)); Raise(nameof(IsFailed)); } }
    }
    public bool IsPending => _state is ProposalState.Pending or ProposalState.Failed;
    public bool IsApplied => _state == ProposalState.Applied;
    public bool IsRejected => _state == ProposalState.Rejected;
    public bool IsFailed => _state == ProposalState.Failed;
    public string Message { get => _message; private set => Set(ref _message, value); }

    private void Apply()
    {
        try
        {
            CodeChange.Apply(_root, Proposal);
            Message = Loc.Format("Ai.Prop.Written", Path);
            State = ProposalState.Applied;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppPaths_Log(ex);
            Message = ex is InvalidOperationException ? ex.Message : Loc.Get("Ai.Prop.WriteFailed");
            State = ProposalState.Failed;
        }
    }

    private static void AppPaths_Log(Exception ex) => ReverseX.Core.AppPaths.Log("Apply proposal failed", ex);
}
