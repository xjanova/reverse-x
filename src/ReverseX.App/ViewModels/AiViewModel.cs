using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Ai;
using ReverseX.Core.Analysis;
using ReverseX.Core.Files;
using ReverseX.Core.Rea;

namespace ReverseX.App.ViewModels;

/// <summary>Everything the AI page and the settings page share. Tests and the screenshot mode swap the factories for fakes.</summary>
public sealed class AiServices
{
    public ApiKeyStore Keys { get; init; } = new();
    public AiPrefs Prefs { get; init; } = AiPrefs.Load();
    public string PrefsPath { get; init; } = AiPrefs.DefaultPath;
    public Func<IModelStreamer>? StreamerFactory { get; init; }
    public Func<string, FileKind, CancellationToken, Task<ReaToolSession>>? ToolFactory { get; init; }

    public AiProvider Provider => AiProviders.Get(Prefs.Provider);
    /// <summary>The key store of the selected vendor (each vendor keeps its own encrypted key).</summary>
    public ApiKeyStore ActiveKeys => Keys.ForProvider(Provider.Id);
    /// <summary>Ready to ask: a key is stored, or the vendor (local server) needs none.</summary>
    public bool Ready => !Provider.NeedsKey || ActiveKeys.HasKey;

    public IModelStreamer CreateStreamer()
    {
        if (StreamerFactory is not null) return StreamerFactory();
        var p = Provider;
        var keys = ActiveKeys;
        return p.IsAnthropic
            ? new AnthropicTurnStreamer(() => keys.Load())
            : new OpenAiCompatTurnStreamer(AiProviders.BaseUrl(Prefs), () => keys.Load(), p.NeedsKey, p.UsesMaxCompletionTokens);
    }
    public Task<ReaToolSession> StartTools(string path, FileKind kind, CancellationToken ct) =>
        ToolFactory is not null ? ToolFactory(path, kind, ct) : ReaToolSession.StartAsync(path, kind, ct);
}

public sealed record SuggestionVm(string Text, bool Primary);

public sealed class AiViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AiServices _svc;
    private readonly List<AiMessage> _historyStore = new();
    private IReadOnlyList<AiMessage> _history = Array.Empty<AiMessage>();
    private CancellationTokenSource? _cts;
    private ReaToolSession? _tools;
    private string? _targetPath;
    private FileKind _targetKind;
    private string _input = "";
    private bool _isRunning;
    private bool _hasKey;
    private decimal _runCost;
    private decimal _sessionCost;
    private string _targetName = "";

    public AiViewModel(AiServices svc)
    {
        _svc = svc;
        _hasKey = svc.Ready;
        SendCommand = new RelayCommand(() => _ = SendAsync(null), () => !_isRunning);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => _isRunning);
        SuggestCommand = new RelayCommand(o => _ = SendAsync(o as string), _ => !_isRunning);
        PickOwnFolderCommand = new RelayCommand(PickOwnFolder);
        ClearOwnFolderCommand = new RelayCommand(() => { OwnFolder = null; });
        NewChatCommand = new RelayCommand(NewChat, () => !_isRunning);
        GoSettingsCommand = new RelayCommand(() => GoSettingsRequested?.Invoke());
        ExportCommand = new RelayCommand(Export);
        _ownFolder = svc.Prefs.OwnProjectFolder is { } f && Directory.Exists(f) ? f : null;
        Evidence.CollectionChanged += (_, _) => RaiseCounts();
        Unknowns.CollectionChanged += (_, _) => RaiseCounts();
        Items.CollectionChanged += (_, _) => { Raise(nameof(ShowWelcome)); Raise(nameof(HasConversation)); Raise(nameof(ShowConversation)); };
        Loc.Changed += RefreshText;
    }

    public event Action? GoSettingsRequested;

    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<FlowNodeVm> PathNodes { get; } = new();
    public ObservableCollection<EvidenceItemVm> Evidence { get; } = new();
    public ObservableCollection<string> Unknowns { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SuggestCommand { get; }
    public ICommand PickOwnFolderCommand { get; }
    public ICommand ClearOwnFolderCommand { get; }
    public ICommand NewChatCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand ExportCommand { get; }

    public string Input { get => _input; set => Set(ref _input, value); }
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) { Raise(nameof(CanSend)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool CanSend => !_isRunning;
    public bool HasKey { get => _hasKey; private set { if (Set(ref _hasKey, value)) Raise(nameof(ShowNoKey)); } }
    public bool ShowNoKey => _isPro && !_hasKey;
    public bool ShowWelcome => _isPro && Items.Count == 0 && _hasKey;
    /// <summary>Free plan: the assistant (and the code it writes into the user's project) is Pro.</summary>
    public bool ShowProLock => !_isPro;
    public bool IsPro => _isPro;
    private bool _isPro = true;

    public void SetPro(bool pro)
    {
        if (_isPro == pro) return;
        _isPro = pro;
        if (!pro) _cts?.Cancel();
        Raise(nameof(IsPro)); Raise(nameof(ShowProLock)); Raise(nameof(ShowNoKey)); Raise(nameof(ShowWelcome)); Raise(nameof(ShowConversation));
    }
    public bool HasConversation => Items.Count > 0;
    public bool ShowConversation => _isPro && Items.Count > 0;
    public bool HasPath => PathNodes.Count > 0;
    public bool HasNoEvidence => Evidence.Count == 0;
    public bool HasNoUnknowns => Unknowns.Count == 0;

    public string TargetName { get => _targetName; private set => Set(ref _targetName, value); }
    public bool HasTarget => _targetPath is not null;
    public string TargetChip => _targetPath is null ? Loc.Get("Ai.NoTarget") : $"{TargetName} · {Loc.Get("Kind." + _targetKind)}";
    public string ModelName => _svc.Provider.IsAnthropic ? AiModels.Get(_svc.Prefs.Model).Display : AiProviders.ModelId(_svc.Prefs);
    public string EvidencePill => Loc.Format("Ai.Pill.Evidence", Evidence.Count, Unknowns.Count);
    public string CostText => Loc.Format("Ai.Cost", CostCalculator.Format(_runCost), CostCalculator.Format(_svc.Prefs.BudgetUsd), CostCalculator.Format(_sessionCost));

    private string? _ownFolder;
    public string? OwnFolder
    {
        get => _ownFolder;
        set
        {
            if (!Set(ref _ownFolder, value)) return;
            _svc.Prefs.OwnProjectFolder = value;
            _svc.Prefs.Save(_svc.PrefsPath);
            Raise(nameof(HasOwnFolder)); Raise(nameof(OwnFolderChip)); RefreshText();
        }
    }
    public bool HasOwnFolder => _ownFolder is not null;
    public string OwnFolderChip => _ownFolder is null ? "" : Loc.Format("Ai.OwnProject", _ownFolder);

    public IReadOnlyList<SuggestionVm> Suggestions
    {
        get
        {
            var l = new List<SuggestionVm>();
            if (HasOwnFolder) l.Add(new SuggestionVm(Loc.Format("Ai.Sug.Build", Path.GetFileName(_ownFolder!.TrimEnd('\\', '/'))), true));
            l.Add(new SuggestionVm(Loc.Get("Ai.Sug.Simpler"), false));
            l.Add(new SuggestionVm(Loc.Get("Ai.Sug.Perms"), false));
            l.Add(new SuggestionVm(Loc.Get("Ai.Sug.Overview"), false));
            return l;
        }
    }

    private void RaiseCounts()
    {
        Raise(nameof(EvidencePill)); Raise(nameof(HasNoEvidence)); Raise(nameof(HasNoUnknowns));
    }

    private void RefreshText()
    {
        Raise(nameof(TargetChip)); Raise(nameof(ModelName)); Raise(nameof(EvidencePill)); Raise(nameof(CostText));
        Raise(nameof(OwnFolderChip)); Raise(nameof(Suggestions));
    }

    /// <summary>Called when settings changed (key saved/removed, model, budget).</summary>
    public void RefreshSettings()
    {
        HasKey = _svc.Ready;
        Raise(nameof(ShowWelcome));
        RefreshText();
    }

    /// <summary>The project opened on another page becomes the subject of the chat. A different project starts a fresh conversation.</summary>
    public void SetTarget(string path, FileKind kind)
    {
        if (string.Equals(_targetPath, path, StringComparison.OrdinalIgnoreCase)) return;
        if (IsRunning) return;
        _targetPath = path;
        _targetKind = kind;
        TargetName = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;
        Raise(nameof(HasTarget));
        NewChat();
        _ = ReleaseToolsAsync();
        RefreshText();
    }

    private async Task ReleaseToolsAsync()
    {
        var t = _tools;
        _tools = null;
        if (t is null) return;
        try { await t.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppPaths.Log("REA session dispose failed", ex); }
    }

    private void NewChat()
    {
        _history = Array.Empty<AiMessage>();
        Items.Clear(); PathNodes.Clear(); Evidence.Clear(); Unknowns.Clear();
        _runCost = 0; _sessionCost = 0;
        Raise(nameof(HasPath)); Raise(nameof(CostText));
    }

    private void PickOwnFolder()
    {
        var dir = Dialogs.PickFolder(Loc.Get("Ai.PickOwn.Title"));
        if (dir is not null) OwnFolder = dir;
    }

    private static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public async Task SendAsync(string? text)
    {
        var q = (text ?? Input).Trim();
        if (q.Length == 0 || IsRunning) return;
        if (!_isPro || !HasKey) { GoSettingsRequested?.Invoke(); return; }

        Input = "";
        Items.Add(new ChatMessageVm { IsUser = true, Text = q });
        var bot = new ChatMessageVm { IsRunning = true, StatusChip = Loc.Get("Ai.Status.Starting") };
        Items.Add(bot);
        IsRunning = true;
        _runCost = 0;
        Raise(nameof(CostText));
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var stepNo = 0;

        void Handle(AgentEvent e)
        {
            switch (e)
            {
                case TextDelta t: bot.IsThinking = false; bot.Text += t.Text; break;
                case ThinkingDelta: bot.IsThinking = true; break;
                case StepStarted s:
                    bot.IsThinking = false;
                    bot.Steps.Add(new StepVm(s.Id, s.Tool, s.Title, s.Detail));
                    stepNo++;
                    bot.StatusChip = Loc.Format("Ai.Status.Step", stepNo);
                    break;
                case StepFinished f:
                    var st = bot.Steps.FirstOrDefault(x => x.Id == f.Id);
                    if (st is not null)
                    {
                        st.State = f.Ok ? StepState.Ok : StepState.Failed;
                        st.Elapsed = f.Elapsed.TotalSeconds < 10 ? $"{f.Elapsed.TotalSeconds:0.0}s" : $"{f.Elapsed.TotalSeconds:0}s";
                        st.Summary = f.Summary;
                    }
                    break;
                case FindingRecorded r: AddFinding(r.Finding); break;
                case ProposalReady p when _ownFolder is not null: Items.Add(new ProposalVm(_ownFolder, p.Proposal)); break;
                case CostUpdated c: _runCost = c.CostUsd; Raise(nameof(CostText)); break;
            }
        }

        try
        {
            var tools = await EnsureToolsAsync(bot, ct);
            var box = new AgentToolbox(tools?.Caller ?? new NoReaCaller(), tools?.Tools ?? Array.Empty<ReaTool>(), _targetPath, _ownFolder, e => Post(() => Handle(e)));
            var agent = new AiAgent(_svc.CreateStreamer(), box, e => Post(() => Handle(e)));
            var opt = new AgentOptions
            {
                ModelId = AiProviders.ModelId(_svc.Prefs),
                BudgetUsd = _svc.Prefs.BudgetUsd,
                TargetDescription = _targetPath is null ? "" : $"{TargetName} ({Loc.Get("Kind." + _targetKind)}) at {_targetPath}",
                HasOwnProject = _ownFolder is not null,
            };
            var res = await Task.Run(() => agent.RunAsync(_history, q, opt, ct), CancellationToken.None);
            _history = res.History;
            _sessionCost += res.CostUsd;
            if (res.Message is not null && res.Outcome != AgentOutcome.Done)
            {
                bot.Notice = res.Message;
                bot.NoticeIsError = res.Outcome is AgentOutcome.Refused or AgentOutcome.Failed;
            }
            foreach (var s in bot.Steps.Where(s => s.IsRunning)) s.State = StepState.Failed;
        }
        catch (Exception ex)
        {
            AppPaths.Log("AI send failed", ex);
            bot.Notice = ErrorText.For(ex);
            bot.NoticeIsError = true;
        }
        finally
        {
            bot.IsRunning = false; bot.IsThinking = false; bot.StatusChip = "";
            IsRunning = false;
            _cts?.Dispose(); _cts = null;
            Raise(nameof(CostText));
        }
    }

    private sealed class NoReaCaller : IReaToolCaller
    {
        public Task<ReaToolResult> CallAsync(string tool, System.Text.Json.Nodes.JsonNode args, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("no REA session");
    }

    private async Task<ReaToolSession?> EnsureToolsAsync(ChatMessageVm bot, CancellationToken ct)
    {
        if (_targetPath is null) return null;
        if (_tools is not null) return _tools;
        bot.StatusChip = Loc.Get("Ai.Status.StartRea");
        try
        {
            _tools = await _svc.StartTools(_targetPath, _targetKind, ct);
            return _tools;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppPaths.Log("REA tools for chat unavailable", ex);
            bot.Notice = Loc.Get("Ai.NoRea");
            return null;
        }
    }

    private void AddFinding(Finding f)
    {
        switch (f.Kind)
        {
            case FindingKind.Evidence:
                if (!Evidence.Any(e => e.Text == f.Text && e.Location == (f.Location ?? ""))) Evidence.Add(new EvidenceItemVm(f));
                break;
            case FindingKind.Unknown:
                if (!Unknowns.Contains(f.Text)) Unknowns.Add(f.Text);
                break;
            case FindingKind.Path:
                PathNodes.Clear();
                for (var i = 0; i < f.Steps.Count; i++)
                {
                    var icon = i == 0 ? "web.png" : i == f.Steps.Count - 1 ? "evidence.png" : i % 2 == 1 ? "javascript.png" : "binary.png";
                    PathNodes.Add(new FlowNodeVm(f.Steps[i], icon, i == f.Steps.Count - 2));
                }
                Raise(nameof(HasPath));
                break;
        }
    }

    private void Export()
    {
        var file = Dialogs.SaveFile(Loc.Get("Ai.Export.Title"), "Markdown (*.md)|*.md", "reverse-x-report.md");
        if (file is null) return;
        try { File.WriteAllText(file, BuildReport(), new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppPaths.Log("Report export failed", ex); }
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Reverse X — {TargetName}").AppendLine();
        foreach (var m in Items.OfType<ChatMessageVm>())
            sb.AppendLine(m.IsUser ? "## ❓ " + m.Text : m.Text).AppendLine();
        sb.AppendLine("## " + Loc.Get("Ai.Evidence"));
        foreach (var e in Evidence) sb.AppendLine($"- [{e.Badge}] `{e.Location}` {e.Text}");
        sb.AppendLine().AppendLine("## " + Loc.Get("Ai.Unknowns"));
        foreach (var u in Unknowns) sb.AppendLine("- " + u);
        return sb.ToString();
    }

    /// <summary>Screenshot mode: fills the page with the mock's sample conversation (no API call).</summary>
    public void LoadDemo(bool running)
    {
        _hasKey = true; Raise(nameof(HasKey)); Raise(nameof(ShowNoKey));
        _targetPath = "NoteFlow 3.2"; _targetKind = FileKind.Asar; TargetName = "NoteFlow 3.2";
        Raise(nameof(HasTarget));
        Items.Clear(); PathNodes.Clear(); Evidence.Clear(); Unknowns.Clear();
        Items.Add(new ChatMessageVm { IsUser = true, Text = Loc.Get("Ai.Demo.Q") });
        var bot = new ChatMessageVm { IsRunning = running, StatusChip = running ? Loc.Format("Ai.Status.Step", 4) : "", Text = Loc.Get("Ai.Demo.A") };
        void Step(string tool, string title, string detail, string ms, bool run = false)
        {
            var s = new StepVm(tool, tool, title, detail) { };
            if (!run) { s.State = StepState.Ok; s.Elapsed = ms; }
            bot.Steps.Add(s);
        }
        Step("analyze_javascript_application", Loc.Get("Ai.Demo.S1"), "3,912 modules · 41 IPC", "1.8s");
        Step("trace_application_feature", Loc.Get("Ai.Demo.S2"), "renderer/editor/clipboard.js:212", "0.9s");
        Step("trace_call_path", Loc.Get("Ai.Demo.S3"), "invoke(\"app:clipboard-write\")", "2.4s");
        Step("trace_javascript_semantics", Loc.Get("Ai.Demo.S4"), "main/ipc.js", "", running);
        Items.Add(bot);
        foreach (var (n, i) in new[] { "Ai.Demo.N1", "Ai.Demo.N2", "Ai.Demo.N3", "Ai.Demo.N4", "Ai.Demo.N5" }.Select((n, i) => (n, i)))
            PathNodes.Add(new FlowNodeVm(Loc.Get(n), new[] { "web.png", "javascript.png", "binary.png", "ai.png", "evidence.png" }[i], i == 3));
        Raise(nameof(HasPath));
        void Ev(EvidenceLevel l, string loc, string t) => Evidence.Add(new EvidenceItemVm(l, loc, t));
        Ev(EvidenceLevel.Seen, "clipboard.js:212", Loc.Get("Ai.Demo.E1"));
        Ev(EvidenceLevel.Seen, "preload.js:48", "exposeInMainWorld(\"noteflow\")");
        Ev(EvidenceLevel.Seen, "main/ipc.js:131", "handle(\"app:clipboard-write\")");
        Ev(EvidenceLevel.Seen, "main/ipc.js:140", "clipboard.write({ text, html })");
        Ev(EvidenceLevel.Inferred, "serialize.js:77", Loc.Get("Ai.Demo.E5"));
        Ev(EvidenceLevel.Inferred, "sanitize.js:15", Loc.Get("Ai.Demo.E6"));
        Unknowns.Add(Loc.Get("Ai.Demo.U1"));
        Unknowns.Add(Loc.Get("Ai.Demo.U2"));
        _runCost = 0.0421m; _sessionCost = 0.0421m;
        _ownFolder = @"D:\Code\MyNotes";
        Raise(nameof(OwnFolderChip)); Raise(nameof(HasOwnFolder)); Raise(nameof(HasNoEvidence)); Raise(nameof(HasNoUnknowns));
        RefreshText();
        if (!running)
        {
            var p = CodeChange.Propose(Path.GetTempPath(), "demo/clipboard-bridge.js",
                "contextBridge.exposeInMainWorld(\"notes\", {\n  copyRich: (d) => ipcRenderer.invoke(\"clipboard:write\", d)\n});\n", Loc.Get("Ai.Demo.P"), out _);
            Items.Add(new ProposalVm(Path.GetTempPath(), p));
        }
        IsRunning = running;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await ReleaseToolsAsync();
    }
}
