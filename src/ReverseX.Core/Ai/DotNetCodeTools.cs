using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ReverseX.Core.Analysis;
using ReverseX.Core.Decompile;

namespace ReverseX.Core.Ai;

/// <summary>
/// The opened .NET assembly read in-process for the assistant: its type/method list (<see cref="ManagedListing"/>) and C#
/// (<see cref="DotNetDecompiler"/>). Both are built on first use and kept, so a chat pays the slow type-system build once.
/// Only <see cref="Path"/> is ever read — the model names members, never files. Problems come back as a short
/// <see cref="ToolOutput"/> error the model can act on; the detail goes to the log.
/// </summary>
public sealed class DotNetCodeTools
{
    private const int MaxOverloads = 10;
    private const int MaxCandidates = 10;
    private const int MaxNamedMethods = 40;

    private static readonly Regex Generic = new(@"`\d+|<[^<>]*>");

    private readonly object _modelGate = new();
    private readonly object _decompilerGate = new();
    private DotNetModel? _model;
    private DotNetDecompiler? _decompiler;

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public DotNetCodeTools(string path) => Path = System.IO.Path.GetFullPath(path);

    private DotNetModel Model(CancellationToken ct)
    {
        lock (_modelGate) return _model ??= ManagedListing.Build(Path, ct);
    }

    private DotNetDecompiler Decompiler(CancellationToken ct)
    {
        lock (_decompilerGate)
        {
            ct.ThrowIfCancellationRequested();
            return _decompiler ??= DotNetDecompiler.Open(Path);
        }
    }

    // ───────── list ─────────

    /// <summary>Types and methods with their tokens; <paramref name="filter"/> matches type full names and method names, ignoring case.</summary>
    public ToolOutput List(string? filter, bool typesOnly, CancellationToken ct) => Guard("the member list", () =>
    {
        var model = Model(ct);
        var f = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        bool Hit(string s) => s.Contains(f!, StringComparison.OrdinalIgnoreCase);

        var rows = new List<(DotNetType Type, List<DotNetMethod> Methods)>();
        foreach (var t in model.Types)
        {
            ct.ThrowIfCancellationRequested();
            if (f is null || Hit(t.FullName)) rows.Add((t, t.Methods));
            else if (t.Methods.Where(m => Hit(m.Name)).ToList() is { Count: > 0 } ms) rows.Add((t, ms));
        }

        var body = new StringBuilder();
        // compiler-generated types ("<>c__DisplayClass…", regex generators) last: the decompiler folds them into their owners anyway
        foreach (var (t, ms) in rows.OrderBy(r => r.Type.FullName.Contains('<')))
        {
            body.Append(t.Token).Append(' ').Append(t.FullName).Append(" · methods: ").Append(Num(t.Methods.Count)).Append(", fields: ").Append(Num(t.FieldCount));
            if (ms.Count != t.Methods.Count) body.Append(" · ").Append(Num(ms.Count)).Append(" match");
            body.Append('\n');
            if (typesOnly) continue;
            foreach (var m in ms)
            {
                body.Append("  ").Append(m.Token).Append(' ').Append(Modifiers(m)).Append(m.Name).Append(m.Signature);
                if (!m.HasBody) body.Append("  [no IL body]");
                body.Append('\n');
            }
        }

        var i = model.Info;
        var sb = new StringBuilder();
        sb.Append("Assembly ").Append(i.Name);
        if (i.Version.Length > 0) sb.Append(' ').Append(i.Version);
        if (i.TargetFramework is { Length: > 0 } tfm) sb.Append(" · ").Append(tfm);
        sb.Append(" · ").Append(Num(model.Types.Count)).Append(" types · ").Append(Num(model.MethodCount)).Append(" methods (read locally from metadata)\n");
        sb.Append("Tokens: 0x02… = type, 0x06… = method; pass one to decompile_dotnet.\n");
        if (f is not null)
        {
            if (rows.Count == 0)
                return new ToolOutput(sb.Append("Nothing matches \"").Append(Short(f)).Append("\" (type full names and method names are searched); try a shorter part of the name.").ToString(), false);
            sb.Append("Filter \"").Append(Short(f)).Append("\": ").Append(Num(rows.Count)).Append(" types, ").Append(Num(rows.Sum(r => r.Methods.Count))).Append(" methods match.\n");
        }
        else if (body.Length > ResultShrinker.DefaultPageChars)
        {
            // the list will be cut: lead with a map of the namespaces so the model can pick a filter instead of paging blindly
            var spaces = model.Types.GroupBy(NamespaceOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            sb.Append("Namespaces (types): ").Append(string.Join(" · ", spaces.Take(60).Select(g => $"{g.Key} ({Num(g.Count())})")));
            if (spaces.Count > 60) sb.Append(" · …").Append(Num(spaces.Count - 60)).Append(" more");
            sb.Append("\nLarge list: call again with a filter (part of a namespace, type or method name) rather than reading every page. ")
              .Append("Compiler-generated types (names with '<') come last.\n");
        }
        return new ToolOutput(sb.Append('\n').Append(body).ToString().TrimEnd('\n'), false);
    });

    private static string NamespaceOf(DotNetType t)
    {
        var outer = t.FullName.Split('+')[0];
        var dot = outer.LastIndexOf('.');
        return dot > 0 ? outer[..dot] : "(global)";
    }

    private static string Modifiers(DotNetMethod m)
    {
        var a = (MethodAttributes)m.Flags;
        var s = (a & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "public ",
            MethodAttributes.Family => "protected ",
            MethodAttributes.FamORAssem => "protected internal ",
            MethodAttributes.Assembly => "internal ",
            MethodAttributes.FamANDAssem => "private protected ",
            MethodAttributes.Private => "private ",
            _ => "",
        };
        if ((a & MethodAttributes.Static) != 0) s += "static ";
        if ((a & MethodAttributes.Abstract) != 0) s += "abstract ";
        else if ((a & MethodAttributes.Virtual) != 0) s += "virtual ";
        if ((a & MethodAttributes.PinvokeImpl) != 0) s += "extern ";
        return s;
    }

    // ───────── decompile ─────────

    /// <summary>
    /// C# of one type or method: by <paramref name="token"/>, or by <paramref name="typeName"/> (+ optional <paramref name="methodName"/>,
    /// every overload). <paramref name="wholeType"/> widens a method to its declaring type.
    /// </summary>
    public ToolOutput Decompile(string? token, string? typeName, string? methodName, bool wholeType, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(token)) return Guard(Short(token), () => ByToken(token.Trim(), wholeType, ct));
        if (!string.IsNullOrWhiteSpace(typeName)) return Guard(Short(typeName), () => ByName(typeName.Trim(), methodName?.Trim(), wholeType, ct));
        return new ToolOutput("give token (0x02… type or 0x06… method, from list_dotnet_members) or type_name (with method_name for one method)", true);
    }

    private ToolOutput ByToken(string token, bool wholeType, CancellationToken ct)
    {
        var model = Model(ct);
        var value = TokenValue(token);
        if (value is null) return BadToken(token);
        var type = model.Types.FirstOrDefault(t => TokenValue(t.Token) == value);
        var method = type is null ? model.Types.SelectMany(t => t.Methods).FirstOrDefault(m => TokenValue(m.Token) == value) : null;
        var dec = Decompiler(ct);

        if (method is not null && wholeType)
        {
            var owner = model.Types.FirstOrDefault(t => t.FullName == method.DeclaringType);
            return Code(Head(owner?.FullName ?? method.DeclaringType, owner?.Token), dec.DecompileDeclaringType(method.Token, ct));
        }
        if (method is not null) return Code(Head(method.DeclaringType + "." + method.Name + method.Signature, method.Token), dec.Decompile(method.Token, ct));
        if (type is not null) return Code(Head(type.FullName, type.Token), dec.Decompile(type.Token, ct));
        // not in the list (e.g. an empty <Module>): the decompiler still checks the token against the assembly's own tables
        return Code(Head(token, null), dec.Decompile(token, ct));
    }

    private ToolOutput ByName(string typeName, string? methodName, bool wholeType, CancellationToken ct)
    {
        var model = Model(ct);
        var types = FindTypes(model, typeName);
        if (types.Count == 0)
            return new ToolOutput($"type \"{Short(typeName)}\" was not found in {FileName}; search with list_dotnet_members (filter = part of the name) and pass the exact full name or its token", true);
        if (types.Count > 1)
            return new ToolOutput($"type name \"{Short(typeName)}\" matches {types.Count} types; call again with one token: " +
                string.Join(", ", types.Take(MaxCandidates).Select(t => t.Token + " " + t.FullName)) + (types.Count > MaxCandidates ? ", …" : ""), true);

        var type = types[0];
        var dec = Decompiler(ct);
        if (string.IsNullOrEmpty(methodName) || wholeType) return Code(Head(type.FullName, type.Token), dec.Decompile(type.Token, ct));

        var methods = type.Methods.Where(m => m.Name == methodName).ToList();
        if (methods.Count == 0) methods = type.Methods.Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (methods.Count == 0)
        {
            var names = type.Methods.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
            return new ToolOutput($"{type.FullName} has no method \"{Short(methodName)}\"; its methods: " +
                (names.Count == 0 ? "(none)" : string.Join(", ", names.Take(MaxNamedMethods)) + (names.Count > MaxNamedMethods ? ", …" : "")), true);
        }

        var sb = new StringBuilder(Head(type.FullName + "." + methods[0].Name, null)).Append('\n');
        foreach (var m in methods.Take(MaxOverloads))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append("\n// ").Append(m.Token).Append(' ').Append(m.Name).Append(m.Signature).Append('\n').Append(dec.Decompile(m.Token, ct).TrimEnd()).Append('\n');
        }
        if (methods.Count > MaxOverloads)
            sb.Append("\n// … ").Append(Num(methods.Count - MaxOverloads)).Append(" more overloads; decompile them by token: ")
              .Append(string.Join(", ", methods.Skip(MaxOverloads).Select(m => m.Token))).Append('\n');
        return new ToolOutput(DotNetDecompiler.Cap(sb.ToString().TrimEnd()), false);
    }

    /// <summary>Exact full name first, then the same name written loosely (dots for '+', any case, no generic arity), then a unique tail ("Outer+Inner", "Parser").</summary>
    private static List<DotNetType> FindTypes(DotNetModel m, string name)
    {
        var exact = m.Types.Where(t => t.FullName == name).ToList();
        if (exact.Count > 0) return exact;
        var key = Loose(name);
        if (key.Length == 0) return exact;
        var loose = m.Types.Where(t => Loose(t.FullName) == key).ToList();
        return loose.Count > 0 ? loose : m.Types.Where(t => Loose(t.FullName).EndsWith("." + key, StringComparison.Ordinal)).ToList();
    }

    private static string Loose(string s)
    {
        for (var i = 0; i < 4 && Generic.IsMatch(s); i++) s = Generic.Replace(s, "");
        return s.Replace('+', '.').Trim().ToLowerInvariant();
    }

    private static int? TokenValue(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private string Head(string what, string? token) =>
        $"// {what}{(token is null ? "" : " · " + token)} · C# decompiled locally from {FileName} (local names and some syntax may differ from the original source)";

    private static ToolOutput Code(string head, string code) => new(DotNetDecompiler.Cap(head + "\n\n" + code.TrimEnd()), false);

    private static ToolOutput BadToken(string token) =>
        new($"\"{Short(token)}\" is not a type (0x02…) or method (0x06…) token of this assembly; copy a token from list_dotnet_members", true);

    // ───────── errors ─────────

    /// <summary>Turns every failure but a cancel into a short English sentence; the exception itself only goes to the log.</summary>
    private ToolOutput Guard(string what, Func<ToolOutput> work)
    {
        try { return work(); }
        catch (OperationCanceledException) { throw; }
        catch (DecompileFailure f)
        {
            AppPaths.Log("AI .NET tool: " + what, f);
            return f.Kind switch
            {
                DecompileFailureKind.NotManaged => new ToolOutput($"{FileName} has no .NET (CLI) metadata, so it cannot be listed or decompiled as C#", true),
                DecompileFailureKind.BadToken => BadToken(what),
                DecompileFailureKind.Io => new ToolOutput($"could not read {FileName} (moved, deleted or locked?)", true),
                _ => new ToolOutput($"the decompiler could not turn {what} into C# (obfuscated or invalid IL?); try the whole type or another member", true),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppPaths.Log("AI .NET tool failed: " + what, ex);
            return new ToolOutput($"reading {what} from {FileName} failed unexpectedly; try another member", true);
        }
    }

    private static string Short(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
