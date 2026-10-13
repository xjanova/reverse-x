using ReverseX.App.Services;
using ReverseX.Core.Files;
using ReverseX.Core.Projects;
using ReverseX.Core.Tools;

namespace ReverseX.App.ViewModels;

public sealed class ProjectItemViewModel : ObservableObject
{
    private readonly DateTime _now;
    public ProjectEntry Entry { get; }

    public ProjectItemViewModel(ProjectEntry entry, DateTime? nowUtc = null)
    {
        Entry = entry;
        _now = nowUtc ?? DateTime.UtcNow;
    }

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string Icon => KindInfo.Icon(Entry.Kind);

    public string Sub
    {
        get
        {
            var parts = new List<string> { Loc.Get(KindInfo.NameKey(Entry.Kind)) };
            if (!string.IsNullOrEmpty(Entry.Detail)) parts.Add(Entry.Detail!);
            if (Entry.SizeBytes > 0) parts.Add(ByteSize.Format(Entry.SizeBytes));
            parts.Add(Ago(Entry.AddedUtc, _now));
            return string.Join(" · ", parts);
        }
    }

    public string StatusText => Loc.Get("Recent.NotAnalyzed");

    public void Refresh() => Raise(string.Empty);

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d.TotalMinutes < 1) return Loc.Get("Time.Now");
        if (d.TotalHours < 1) return Loc.Format("Time.Min", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Loc.Format("Time.Hour", (int)d.TotalHours);
        if (d.TotalDays < 30) return Loc.Format("Time.Day", (int)d.TotalDays);
        return Loc.Format("Time.Month", Math.Max(1, (int)(d.TotalDays / 30)));
    }
}

public sealed class ToolItemViewModel : ObservableObject
{
    private ToolStatus _status;

    public ToolItemViewModel(ToolStatus status) => _status = status;

    public ToolStatus Status
    {
        get => _status;
        set { _status = value; Raise(string.Empty); }
    }

    public string Id => _status.Id;
    public string Name => _status.DisplayName;
    public ToolLevel Level => _status.Level;
    public string Message => Loc.Format("Tool.Msg." + _status.MessageCode, _status.MessageArgs.Cast<object>().ToArray());
    public string LevelText => Loc.Get("Lvl." + _status.Level);
    public string Icon => "/ReverseX;component/Assets/Icons3D/" + _status.Id switch
    {
        "node" => "javascript.png",
        "rea" => "ai.png",
        "ghidra" => "binary.png",
        "jadx" => "android.png",
        "browser" => "web.png",
        _ => "settings.png",
    };

    public void Refresh() => Raise(string.Empty);
}

public sealed class NavItemViewModel : ObservableObject
{
    private bool _selected;
    public NavItemViewModel(string key, string icon) { Key = key; Icon = "/ReverseX;component/Assets/Icons3D/" + icon; }
    public string Key { get; }
    public string Icon { get; }
    public string Title => Loc.Get("Nav." + Key);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public void Refresh() => Raise(nameof(Title));
}

public sealed class PlaceholderViewModel : ObservableObject
{
    public PlaceholderViewModel(NavItemViewModel nav) { Nav = nav; }
    public NavItemViewModel Nav { get; }
    public string Icon => Nav.Icon;
    public string Title => Nav.Title;
    public string Desc => Loc.Get("Page." + Nav.Key + ".Desc");
    public void Refresh() => Raise(string.Empty);
}
