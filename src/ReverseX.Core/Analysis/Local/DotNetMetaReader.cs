using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ReverseX.Core.Analysis.Local;

/// <summary>
/// Reads .NET metadata straight from the PE file (no REA, no loading): identity, types, references, P/Invoke,
/// resources, risky API use, user strings and obfuscator hints.
/// </summary>
public static class DotNetMetaReader
{
    private const int TypeCap = 20000;
    private const int UserStringScanCap = 50000;

    // Member references matched on "Namespace.Type::Member" prefixes.
    private static readonly (string Category, string Prefix)[] RiskyApis =
    {
        ("Process", "System.Diagnostics.Process::Start"),
        ("Reflection", "System.Reflection.Assembly::Load"),
        ("Reflection", "System.Reflection.Assembly::LoadFrom"),
        ("Reflection", "System.Reflection.Assembly::LoadFile"),
        ("Reflection", "System.Reflection.MethodBase::Invoke"),
        ("Reflection", "System.Reflection.Emit."),
        ("Reflection", "System.Activator::CreateInstance"),
        ("Network", "System.Net.Http.HttpClient::"),
        ("Network", "System.Net.WebClient::"),
        ("Network", "System.Net.WebRequest::Create"),
        ("Network", "System.Net.Sockets."),
        ("Network", "System.Net.WebSockets."),
        ("Crypto", "System.Security.Cryptography."),
        ("Registry", "Microsoft.Win32.Registry"),
        ("FileSystem", "System.IO.File::Delete"),
        ("FileSystem", "System.IO.File::WriteAll"),
        ("FileSystem", "System.IO.Directory::Delete"),
        ("Unsafe", "System.Runtime.InteropServices.Marshal::"),
        ("Unsafe", "System.Runtime.CompilerServices.Unsafe::"),
        ("Serialization", "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter::"),
        ("Serialization", "Newtonsoft.Json.JsonConvert::DeserializeObject"),
        ("Environment", "System.Environment::GetEnvironmentVariable"),
        ("Environment", "System.Environment::Exit"),
        ("Clipboard", "System.Windows.Forms.Clipboard::"),
        ("Clipboard", "System.Windows.Clipboard::"),
        ("Debugger", "System.Diagnostics.Debugger::"),
        ("Compression", "System.IO.Compression."),
    };

    private static readonly (string Marker, string Name)[] ObfuscatorMarkers =
    {
        ("ConfusedByAttribute", "ConfuserEx"), ("Confuser.Core", "ConfuserEx"),
        ("DotfuscatorAttribute", "Dotfuscator"), ("SmartAssembly.Attributes", "SmartAssembly"),
        ("PoweredByAttribute", "SmartAssembly"), ("Xenocode.Client.Attributes", "Xenocode"),
        ("BabelObfuscatorAttribute", "Babel"), ("ObfuscatedByGoliath", "Goliath"),
        ("CryptoObfuscator", "Crypto Obfuscator"), ("NETGuard", ".NET Guard"),
        ("NETReactorAttribute", ".NET Reactor"), ("ProtectedWithCryptoObfuscatorAttribute", "Crypto Obfuscator"),
        ("YanoAttribute", "Yano"), ("EazfuscatorAttribute", "Eazfuscator.NET"), ("Agile.NET", "Agile.NET"),
        ("BeelzebubObfuscator", "Beelzebub"), ("ZYXDNGuarder", "DNGuard"), ("__DNGuard", "DNGuard"),
        ("VMProtect", "VMProtect"), ("ArmDot", "ArmDot"),
    };

    public static NetMetaModel Read(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new AnalysisProgress("net.meta", 0, 3));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) throw new InvalidDataException("not a .NET assembly (no CLI metadata)");
        var md = pe.GetMetadataReader();
        var headers = pe.PEHeaders;
        var cor = headers.CorHeader!;

        string? tfm = null, company = null, product = null, title = null, copyright = null, infoVer = null, obf = null;
        var ivt = new List<string>();
        AssemblyName? asmName = null;
        if (md.IsAssembly)
        {
            var def = md.GetAssemblyDefinition();
            asmName = def.GetAssemblyName();
            foreach (var h in def.GetCustomAttributes())
            {
                var (type, value) = Attr(md, h);
                switch (type)
                {
                    case "System.Runtime.Versioning.TargetFrameworkAttribute": tfm = value; break;
                    case "System.Reflection.AssemblyCompanyAttribute": company = value; break;
                    case "System.Reflection.AssemblyProductAttribute": product = value; break;
                    case "System.Reflection.AssemblyTitleAttribute": title = value; break;
                    case "System.Reflection.AssemblyCopyrightAttribute": copyright = value; break;
                    case "System.Reflection.AssemblyInformationalVersionAttribute": infoVer = value; break;
                    case "System.Runtime.CompilerServices.InternalsVisibleToAttribute": if (value != null) ivt.Add(value.Split(',')[0]); break;
                }
                obf ??= ObfuscatorFor(type);
            }
        }
        foreach (var h in md.GetCustomAttributes(EntityHandle.ModuleDefinition))
            obf ??= ObfuscatorFor(Attr(md, h).Type);

        ct.ThrowIfCancellationRequested();
        progress?.Report(new AnalysisProgress("net.types", 1, 3));
        var types = new List<NetTypeInfo>();
        var unreadable = 0;
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var name = md.GetString(t.Name);
            var ns = md.GetString(t.Namespace);
            obf ??= ObfuscatorFor(ns.Length > 0 ? ns + "." + name : name);
            if (name == "<Module>") continue;
            if (IsUnreadable(name)) unreadable++;
            if (types.Count >= TypeCap) continue;
            var attrs = t.Attributes;
            var vis = attrs & TypeAttributes.VisibilityMask;
            var isPublic = vis is TypeAttributes.Public or TypeAttributes.NestedPublic;
            var baseName = t.BaseType.IsNil ? null : TypeName(md, t.BaseType);
            var kind = (attrs & TypeAttributes.Interface) != 0 ? "interface"
                : baseName == "System.Enum" ? "enum"
                : baseName is "System.ValueType" ? "struct"
                : baseName is "System.MulticastDelegate" ? "delegate"
                : (attrs & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed) ? "static class"
                : "class";
            if (!t.GetDeclaringType().IsNil)
            {
                var outer = md.GetTypeDefinition(t.GetDeclaringType());
                ns = md.GetString(outer.Namespace);
                name = md.GetString(outer.Name) + "+" + name;
            }
            types.Add(new NetTypeInfo(ns, name, kind, isPublic, t.GetMethods().Count, t.GetFields().Count, t.GetProperties().Count, baseName));
        }

        var pinvokes = new List<NetPInvoke>();
        foreach (var mh in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(mh);
            if (IsUnreadable(md.GetString(m.Name))) unreadable++;
            var imp = m.GetImport();
            if (imp.Module.IsNil) continue;
            var module = md.GetString(md.GetModuleReference(imp.Module).Name);
            var entry = imp.Name.IsNil ? md.GetString(m.Name) : md.GetString(imp.Name);
            var owner = md.GetTypeDefinition(m.GetDeclaringType());
            pinvokes.Add(new NetPInvoke(module, entry, md.GetString(owner.Name) + "." + md.GetString(m.Name)));
        }

        var refs = md.AssemblyReferences.Select(h =>
        {
            var a = md.GetAssemblyReference(h);
            var tok = a.PublicKeyOrToken.IsNil ? null : Convert.ToHexString(md.GetBlobBytes(a.PublicKeyOrToken)).ToLowerInvariant();
            return new NetRef(md.GetString(a.Name), a.Version.ToString(), tok);
        }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var resources = md.ManifestResources.Select(h =>
        {
            var r = md.GetManifestResource(h);
            long size = 0;
            if (r.Implementation.IsNil && cor.ResourcesDirectory.Size > 0)
                size = ResourceSize(pe, cor.ResourcesDirectory, r.Offset);
            return new NetResource(md.GetString(r.Name), size, (r.Attributes & ManifestResourceAttributes.VisibilityMask) == ManifestResourceAttributes.Public);
        }).ToList();

        ct.ThrowIfCancellationRequested();
        progress?.Report(new AnalysisProgress("net.strings", 2, 3));
        var apiCounts = new Dictionary<(string Cat, string Api), int>();
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var full = ParentName(md, mr.Parent) + "::" + md.GetString(mr.Name);
            foreach (var (cat, prefix) in RiskyApis)
                if (full.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var key = (cat, prefix.EndsWith("::") || prefix.EndsWith('.') ? full : prefix);
                    apiCounts[key] = apiCounts.GetValueOrDefault(key) + 1;
                    break;
                }
        }
        foreach (var p in pinvokes)
        {
            var key = ("PInvoke", p.Module + "!" + p.Entry);
            apiCounts[key] = apiCounts.GetValueOrDefault(key) + 1;
        }
        var api = apiCounts.Select(kv => new NetApiUse(kv.Key.Cat, kv.Key.Api, kv.Value))
            .OrderBy(a => a.Category, StringComparer.Ordinal).ThenByDescending(a => a.Count).ToList();

        var scan = new TextScan.Collector();
        var usCount = 0;
        var us = MetadataTokens.UserStringHandle(1);
        var heapSize = md.GetHeapSize(HeapIndex.UserString);
        while (!us.IsNil && MetadataTokens.GetHeapOffset(us) < heapSize && usCount < UserStringScanCap)
        {
            var s = md.GetUserString(us);
            usCount++;
            if (s.Length >= 10) scan.Scan(s, "#US");
            us = md.GetNextHandle(us);
        }

        var corFlags = cor.Flags;
        var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
        string? entryPoint = null;
        if (cor.EntryPointTokenOrRelativeVirtualAddress != 0 && (corFlags & CorFlags.NativeEntryPoint) == 0)
        {
            var h = MetadataTokens.EntityHandle(cor.EntryPointTokenOrRelativeVirtualAddress);
            if (h.Kind == HandleKind.MethodDefinition)
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                entryPoint = TypeName(md, m.GetDeclaringType()) + "." + md.GetString(m.Name);
            }
        }

        var allTypes = md.TypeDefinitions.Count;
        if (obf == null && allTypes > 20 && unreadable * 3 > allTypes + md.MethodDefinitions.Count)
            obf = "?";

        var model = new NetMetaModel
        {
            Name = asmName?.Name ?? md.GetString(md.GetModuleDefinition().Name),
            Version = asmName?.Version?.ToString() ?? "",
            Culture = string.IsNullOrEmpty(asmName?.CultureName) ? null : asmName!.CultureName,
            PublicKeyToken = asmName?.GetPublicKeyToken() is { Length: > 0 } pkt ? Convert.ToHexString(pkt).ToLowerInvariant() : null,
            TargetFramework = tfm,
            Company = company,
            Product = product,
            Title = title,
            Copyright = copyright,
            InformationalVersion = infoVer,
            Mvid = mvid.ToString(),
            MetadataVersion = md.MetadataVersion,
            Machine = MachineName(headers.CoffHeader.Machine, corFlags),
            IsDll = headers.IsDll,
            ILOnly = (corFlags & CorFlags.ILOnly) != 0,
            ReadyToRun = cor.ManagedNativeHeaderDirectory.Size > 0,
            StrongNameSigned = (corFlags & CorFlags.StrongNameSigned) != 0,
            EntryPoint = entryPoint,
            Obfuscator = obf,
            UnreadableNames = unreadable,
            UserStringCount = usCount,
            InternalsVisibleTo = ivt,
            Types = types,
            References = refs,
            PInvokes = pinvokes,
            Resources = resources,
            ApiUse = api,
            Urls = scan.UrlList,
            Secrets = scan.SecretList,
        };
        progress?.Report(new AnalysisProgress("net.strings", 3, 3));
        return model with { Insights = Insights(model) };
    }

    private static List<Insight> Insights(NetMetaModel m)
    {
        var list = new List<Insight>();
        if (m.Obfuscator is "?") list.Add(Insight.Of("NetObfuscatedNames", InsightTone.Warn, EvidenceLevel.Inferred, m.UnreadableNames));
        else if (m.Obfuscator != null) list.Add(Insight.Of("NetObfuscator", InsightTone.Warn, EvidenceLevel.Seen, m.Obfuscator));
        if (!m.ILOnly) list.Add(Insight.Of("NetMixedMode", InsightTone.Info, EvidenceLevel.Seen));
        if (m.ReadyToRun) list.Add(Insight.Of("NetReadyToRun", InsightTone.Info, EvidenceLevel.Seen));
        if (m.PInvokes.Count > 0)
            list.Add(Insight.Of("NetPInvoke", InsightTone.Info, EvidenceLevel.Seen, m.PInvokes.Count, string.Join(", ", m.PInvokes.Select(p => p.Module).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))));
        foreach (var cat in new[] { "Process", "Reflection", "Network", "Registry", "Serialization", "Debugger" })
        {
            var n = m.ApiUse.Where(a => a.Category == cat).Sum(a => a.Count);
            if (n > 0) list.Add(Insight.Of("NetApi" + cat, cat is "Process" or "Serialization" ? InsightTone.Warn : InsightTone.Info, EvidenceLevel.Seen, n));
        }
        if (m.ApiUse.Any(a => a.Api.Contains("BinaryFormatter", StringComparison.Ordinal)))
            list.Add(Insight.Of("NetBinaryFormatter", InsightTone.Risk, EvidenceLevel.Seen));
        if (m.Secrets.Count > 0) list.Add(Insight.Of("Secrets", InsightTone.Risk, EvidenceLevel.Seen, m.Secrets.Count));
        if (m.InternalsVisibleTo.Count > 0) list.Add(Insight.Of("NetInternalsVisibleTo", InsightTone.Info, EvidenceLevel.Seen, string.Join(", ", m.InternalsVisibleTo)));
        if (m.StrongNameSigned) list.Add(Insight.Of("NetStrongName", InsightTone.Good, EvidenceLevel.Seen));
        return list;
    }

    private static string? ObfuscatorFor(string name)
    {
        foreach (var (marker, n) in ObfuscatorMarkers)
            if (name.Contains(marker, StringComparison.Ordinal)) return n;
        return null;
    }

    // Names made of control/private-use/zero-width chars, or long runs of one invisible symbol, are obfuscator output.
    private static bool IsUnreadable(string name)
    {
        if (name.Length == 0 || name.StartsWith('<') || name.StartsWith('.')) return false;
        foreach (var c in name)
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned
                    or System.Globalization.UnicodeCategory.Surrogate)
                return true;
        return name.Length > 1 && name.All(ch => ch > 0x2e7f) && !name.Any(char.IsLetter);
    }

    private static (string Type, string? Value) Attr(MetadataReader md, CustomAttributeHandle h)
    {
        var a = md.GetCustomAttribute(h);
        EntityHandle owner = a.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType(),
            _ => default,
        };
        var type = owner.IsNil ? "" : TypeName(md, owner);
        string? value = null;
        try
        {
            var blob = md.GetBlobReader(a.Value);
            if (blob.Length > 4 && blob.ReadUInt16() == 1) value = blob.ReadSerializedString();
        }
        catch (BadImageFormatException) { }
        return (type, value);
    }

    private static string ParentName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference or HandleKind.TypeDefinition => TypeName(md, parent),
        HandleKind.TypeSpecification => SpecName(md, (TypeSpecificationHandle)parent),
        _ => "",
    };

    // Generic instantiations (List<int>) are stored as a TypeSpec blob: GENERICINST CLASS|VALUETYPE <TypeDefOrRef> …
    private static string SpecName(MetadataReader md, TypeSpecificationHandle h)
    {
        try
        {
            var b = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
            if (b.ReadByte() != 0x15) return "";
            b.ReadByte();
            return TypeName(md, b.ReadTypeHandle());
        }
        catch (BadImageFormatException) { return ""; }
    }

    private static string TypeName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference:
            {
                var r = md.GetTypeReference((TypeReferenceHandle)h);
                var name = md.GetString(r.Name);
                if (r.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(md, (EntityHandle)r.ResolutionScope) + "+" + name;
                var ns = md.GetString(r.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            case HandleKind.TypeDefinition:
            {
                var t = md.GetTypeDefinition((TypeDefinitionHandle)h);
                var name = md.GetString(t.Name);
                if (!t.GetDeclaringType().IsNil) return TypeName(md, t.GetDeclaringType()) + "+" + name;
                var ns = md.GetString(t.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            case HandleKind.TypeSpecification:
                return SpecName(md, (TypeSpecificationHandle)h);
            default:
                return "";
        }
    }

    private static long ResourceSize(PEReader pe, DirectoryEntry dir, long offset)
    {
        try
        {
            var block = pe.GetSectionData(dir.RelativeVirtualAddress);
            if (offset + 4 > dir.Size) return 0;
            var r = block.GetReader((int)offset, 4);
            return r.ReadUInt32();
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException) { return 0; }
    }

    private static string MachineName(Machine m, CorFlags f) => m switch
    {
        Machine.I386 when (f & CorFlags.Requires32Bit) != 0 => "x86",
        Machine.I386 when (f & CorFlags.Prefers32Bit) != 0 => "AnyCPU (32-bit preferred)",
        Machine.I386 => "AnyCPU",
        Machine.Amd64 => "x64",
        Machine.Arm64 => "ARM64",
        Machine.ArmThumb2 or Machine.Arm => "ARM",
        _ => m.ToString(),
    };
}
