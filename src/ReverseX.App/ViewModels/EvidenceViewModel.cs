using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Analysis;

namespace ReverseX.App.ViewModels;

public sealed record EvidenceItem(string Source, string Target, EvidenceLevel Level, string Badge, string Text);

public sealed record EvidenceGroup(string Title, string Path, IReadOnlyList<EvidenceItem> Items);

/// <summary>Everything the analysis pages currently show as evidence, in one list, filterable by level and exportable as Markdown.</summary>
public sealed class EvidenceViewModel : ObservableObject
{
    private readonly Func<IEnumerable<IEvidenceSource>> _sources;
    private readonly Func<string, string?> _saveDialog;
    private IReadOnlyList<EvidenceItem> _all = Array.Empty<EvidenceItem>();
    private IReadOnlyList<EvidenceGroup> _groups = Array.Empty<EvidenceGroup>();
    private EvidenceLevel? _filter;
    private string _notice = "";

    public EvidenceViewModel(Func<IEnumerable<IEvidenceSource>> sources, Func<string, string?>? saveDialog = null)
    {
        _sources = sources;
        _saveDialog = saveDialog ?? (name => Dialogs.SaveFile(Loc.Get("Evi.Export"), "Markdown|*.md", name));
        ExportCommand = new RelayCommand(Export, () => _all.Count > 0);
        Loc.Changed += Refresh;
    }

    public ICommand ExportCommand { get; }

    public IReadOnlyList<EvidenceGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
    public bool IsEmpty => _all.Count == 0;
    public bool HasItems => _all.Count > 0;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }

    public string AllText => Loc.Format("Evi.All", _all.Count);
    public string SeenText => Loc.Format("Evi.Seen", _all.Count(i => i.Level == EvidenceLevel.Seen));
    public string InferredText => Loc.Format("Evi.Inferred", _all.Count(i => i.Level == EvidenceLevel.Inferred));
    public string UnknownText => Loc.Format("Evi.Unknown", _all.Count(i => i.Level == EvidenceLevel.Unknown));

    public bool ShowAll { get => _filter is null; set { if (value) SetFilter(null); } }
    public bool ShowSeen { get => _filter == EvidenceLevel.Seen; set { if (value) SetFilter(EvidenceLevel.Seen); } }
    public bool ShowInferred { get => _filter == EvidenceLevel.Inferred; set { if (value) SetFilter(EvidenceLevel.Inferred); } }
    public bool ShowUnknown { get => _filter == EvidenceLevel.Unknown; set { if (value) SetFilter(EvidenceLevel.Unknown); } }

    public IReadOnlyList<EvidenceItem> Items => _all;

    /// <summary>Re-reads the pages. Called whenever the Evidence page is shown.</summary>
    public void Refresh()
    {
        _all = _sources()
            .Where(s => s.EvidenceTarget is not null)
            .SelectMany(s => s.EvidenceItems.Select(r => new EvidenceItem(s.EvidenceSourceName, s.EvidenceTarget!, r.Level, r.Badge, r.Text)))
            .ToList();
        Notice = "";
        Regroup();
        foreach (var n in new[] { nameof(IsEmpty), nameof(HasItems), nameof(AllText), nameof(SeenText), nameof(InferredText), nameof(UnknownText), nameof(Items) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetFilter(EvidenceLevel? level)
    {
        _filter = level;
        foreach (var n in new[] { nameof(ShowAll), nameof(ShowSeen), nameof(ShowInferred), nameof(ShowUnknown) }) Raise(n);
        Regroup();
    }

    private void Regroup()
    {
        Groups = _all
            .Where(i => _filter is null || i.Level == _filter)
            .GroupBy(i => (i.Source, i.Target))
            .Select(g => new EvidenceGroup(g.Key.Source + " · " + Path.GetFileName(g.Key.Target.TrimEnd('\\', '/')), g.Key.Target, g.ToList()))
            .ToList();
    }

    /// <summary>Markdown report of the current filter. Public for tests.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + Loc.Get("Evi.Report.Title"));
        sb.AppendLine();
        sb.AppendLine(Loc.Format("Evi.Report.When", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        sb.AppendLine();
        sb.AppendLine($"- {AllText}");
        sb.AppendLine($"- {SeenText} · {InferredText} · {UnknownText}");
        foreach (var g in Groups)
        {
            sb.AppendLine();
            sb.AppendLine("## " + Md(g.Title));
            sb.AppendLine();
            sb.AppendLine("`" + g.Path.Replace("`", "'") + "`");
            sb.AppendLine();
            sb.AppendLine("| " + Loc.Get("Evi.Col.Level") + " | " + Loc.Get("Evi.Col.Text") + " |");
            sb.AppendLine("|---|---|");
            foreach (var i in g.Items) sb.AppendLine($"| {Md(i.Badge)} | {Md(i.Text)} |");
        }
        return sb.ToString();
    }

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private void Export()
    {
        var file = _saveDialog("reversex-evidence-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".md");
        if (file is null) return;
        try
        {
            File.WriteAllText(file, ToMarkdown(), new UTF8Encoding(false));
            Notice = Loc.Format("Evi.Exported", Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Evidence export failed: " + file, ex);
            Notice = Loc.Get("Evi.ExportFailed");
        }
    }
}
