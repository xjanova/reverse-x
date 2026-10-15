using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ReverseX.Core.Files;
using ReverseX.Core.Projects;
using ReverseX.Core.Rea;
using ReverseX.Core.Tools;
using Xunit;

namespace ReverseX.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rx-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class PaletteTests
{
    private static string AppDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir, "src", "ReverseX.App"))) dir = System.IO.Path.GetDirectoryName(dir);
        return System.IO.Path.Combine(dir!, "src", "ReverseX.App");
    }

    private static SortedSet<string> Keys(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(System.IO.Path.Combine(AppDir(), "Theme", file));
        return new SortedSet<string>(doc.Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null)!);
    }

    [Fact]
    public void Night_And_Sky_HaveSameKeys()
    {
        var night = Keys("Palette.Night.xaml");
        var sky = Keys("Palette.Sky.xaml");
        Assert.NotEmpty(night);
        Assert.Empty(night.Except(sky));
        Assert.Empty(sky.Except(night));
    }

    [Fact]
    public void Thai_And_English_Strings_HaveSameKeys()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        SortedSet<string> K(string f) => new(XDocument.Load(System.IO.Path.Combine(AppDir(), "Resources", f)).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null)!);
        var th = K("Strings.th.xaml");
        var en = K("Strings.en.xaml");
        Assert.Empty(th.Except(en));
        Assert.Empty(en.Except(th));
    }

    /// <summary>Every insight code and progress step the local readers emit must have UI text, or the user sees a raw key.</summary>
    [Fact]
    public void LocalReader_InsightCodesAndSteps_HaveStrings()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(System.IO.Path.Combine(AppDir(), "Resources", "Strings.en.xaml")).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToHashSet();
        var src = string.Concat(Directory.GetFiles(System.IO.Path.Combine(AppDir(), "..", "ReverseX.Core", "Analysis", "Local"), "*.cs").Select(File.ReadAllText));

        var needed = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, "Insight\\.Of\\(\"([A-Za-z]+)\"(\\s*\\+\\s*cat)?"))
            needed.AddRange(m.Groups[2].Success
                ? new[] { "Process", "Reflection", "Network", "Registry", "Serialization", "Debugger" }.Select(c => "S.Ins." + m.Groups[1].Value + c)
                : new[] { "S.Ins." + m.Groups[1].Value });
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, "AnalysisProgress\\(\"([a-z.]+)\""))
            needed.Add("S.Step." + m.Groups[1].Value);

        Assert.True(needed.Count > 40);
        Assert.Empty(needed.Distinct().Where(k => !keys.Contains(k)));
    }
}

public class FileKindDetectorTests
{
    [Fact]
    public void Detects_DotNet_Assembly()
    {
        var r = FileKindDetector.Detect(typeof(FileKindDetector).Assembly.Location);
        Assert.Equal(FileKind.DotNet, r.Kind);
    }

    [Fact]
    public void Detects_Sqlite_Elf_Har_Apk_Folder()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.File("a.db"), "SQLite format 3\0"u8.ToArray().Concat(new byte[100]).ToArray());
        Assert.Equal(FileKind.Sqlite, FileKindDetector.Detect(t.File("a.db")).Kind);

        var elf = new byte[64]; elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F'; elf[4] = 2; elf[5] = 1; elf[18] = 0x3E;
        File.WriteAllBytes(t.File("bin"), elf);
        var er = FileKindDetector.Detect(t.File("bin"));
        Assert.Equal(FileKind.Elf, er.Kind);
        Assert.Contains("x86-64", er.Detail);

        File.WriteAllText(t.File("x.har"), "{\"log\":{\"entries\":[]}}");
        Assert.Equal(FileKind.Har, FileKindDetector.Detect(t.File("x.har")).Kind);

        using (var z = ZipFile.Open(t.File("a.apk"), ZipArchiveMode.Create))
            z.CreateEntry("AndroidManifest.xml").Open().Dispose();
        Assert.Equal(FileKind.Apk, FileKindDetector.Detect(t.File("a.apk")).Kind);

        Assert.Equal(FileKind.Folder, FileKindDetector.Detect(t.Path).Kind);
    }

    [Fact]
    public void Detects_Electron_AppFolder_And_Asar()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(t.Path, "resources"));
        var asar = t.File("resources/app.asar");
        File.WriteAllBytes(asar, new byte[] { 4, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });
        var r = FileKindDetector.Detect(t.Path);
        Assert.Equal(FileKind.AppFolder, r.Kind);
        Assert.Equal("Electron", r.Detail);
        Assert.Equal(FileKind.Asar, FileKindDetector.Detect(asar).Kind);
    }

    [Fact]
    public void Missing_Path_Throws_FileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => FileKindDetector.Detect(@"C:\no\such\file.bin"));
    }
}

public class ProjectStoreTests
{
    [Fact]
    public void Add_Load_Remove_RoundTrips_And_SamePathMovesToTop()
    {
        using var t = new TempDir();
        var store = new ProjectStore(t.File("projects.json"));
        Assert.Empty(store.Load());

        var a = t.File("a.db"); var b = t.File("b.db");
        store.Add(a, new FileKindResult(FileKind.Sqlite), 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        store.Add(b, new FileKindResult(FileKind.Elf, "64-bit x86-64"), 20, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var list = new ProjectStore(t.File("projects.json")).Load();
        Assert.Equal(2, list.Count);
        Assert.Equal("b.db", list[0].Name);
        Assert.Equal("64-bit x86-64", list[0].Detail);

        store.Add(a, new FileKindResult(FileKind.Sqlite), 11, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        list = store.Load();
        Assert.Equal(2, list.Count);
        Assert.Equal("a.db", list[0].Name);
        Assert.Equal(11, list[0].SizeBytes);

        store.Remove(a);
        Assert.Single(store.Load());
    }

    [Fact]
    public void Corrupt_File_Loads_As_Empty()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("projects.json"), "{ not json");
        Assert.Empty(new ProjectStore(t.File("projects.json")).Load());
    }
}

public class ToolStatusMapperTests
{
    private static ReaDoctorReport Doctor() =>
        new(false, false, "6.4.0", 170, 20, Array.Empty<ReaDoctorCheck>(), JsonDocument.Parse("{}").RootElement);

    private static ToolStatus Row(IReadOnlyList<ToolStatus> l, string id) => l.Single(s => s.Id == id);

    [Fact]
    public void NoNode_IsRed_AndReaIsOff()
    {
        var l = ToolStatusMapper.Map(new ToolProbeInputs(null, true, null, null, null, null, false, null));
        Assert.Equal(ToolLevel.Bad, Row(l, "node").Level);
        Assert.Equal(ToolLevel.Off, Row(l, "rea").Level);
    }

    [Fact]
    public void GoodNode_And_Doctor_IsGreen_EvenIfDoctorUnhealthy()
    {
        var node = new NodeInfo("node.exe", new Version(24, 11, 1));
        var l = ToolStatusMapper.Map(new ToolProbeInputs(node, true, Doctor(), null, null, 17, false, "Chrome"));
        Assert.Equal(ToolLevel.Ok, Row(l, "node").Level);
        var rea = Row(l, "rea");
        Assert.Equal(ToolLevel.Ok, rea.Level);
        Assert.Contains("170", rea.MessageArgs);
    }

    [Fact]
    public void Java17_Makes_Ghidra_Yellow_NeedJdk21()
    {
        var node = new NodeInfo("node.exe", new Version(24, 11, 1));
        var l = ToolStatusMapper.Map(new ToolProbeInputs(node, true, Doctor(), null, null, 17, false, null));
        var g = Row(l, "ghidra");
        Assert.Equal(ToolLevel.Warn, g.Level);
        Assert.Equal("ghidra.needJdk21", g.MessageCode);
    }

    [Fact]
    public void ReaTimeout_IsRed()
    {
        var node = new NodeInfo("node.exe", new Version(24, 11, 1));
        var l = ToolStatusMapper.Map(new ToolProbeInputs(node, true, null, null, "timeout", null, false, null));
        Assert.Equal(ToolLevel.Bad, Row(l, "rea").Level);
    }
}
