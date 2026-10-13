using System.Text.Json;
using System.Text.Json.Serialization;
using ReverseX.Core.Files;

namespace ReverseX.Core.Projects;

public sealed record ProjectEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FileKind Kind { get; init; }
    public string? Detail { get; init; }
    public long SizeBytes { get; init; }
    public DateTime AddedUtc { get; init; }
}

/// <summary>Recent projects, newest first, kept in projects.json. Same path added again just moves to the top.</summary>
public sealed class ProjectStore
{
    public const int MaxEntries = 100;
    private readonly string _file;
    private readonly object _gate = new();

    public ProjectStore(string? file = null) => _file = file ?? AppPaths.ProjectsJson;

    public IReadOnlyList<ProjectEntry> Load()
    {
        lock (_gate) return ReadLocked();
    }

    public ProjectEntry Add(string path, FileKindResult kind, long sizeBytes, DateTime? nowUtc = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        var name = System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (name.Length == 0) name = full;
        var entry = new ProjectEntry
        {
            Path = full,
            Name = name,
            Kind = kind.Kind,
            Detail = kind.Detail,
            SizeBytes = sizeBytes,
            AddedUtc = nowUtc ?? DateTime.UtcNow,
        };
        lock (_gate)
        {
            var list = ReadLocked().Where(e => !string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            WriteLocked(list);
        }
        return entry;
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            var list = ReadLocked().Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            WriteLocked(list);
        }
    }

    private List<ProjectEntry> ReadLocked()
    {
        try
        {
            if (!File.Exists(_file)) return new();
            return JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not read projects.json", ex);
            return new();
        }
    }

    private void WriteLocked(List<ProjectEntry> list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, overwrite: true);
    }
}
