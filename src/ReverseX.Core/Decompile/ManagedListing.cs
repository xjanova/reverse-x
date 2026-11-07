using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ReverseX.Core.Analysis;

namespace ReverseX.Core.Decompile;

/// <summary>
/// The type/method list of a managed assembly read straight from its metadata — no REA, no size budget. Used when REA cannot list
/// the members (large assemblies exceed its admission budget; Node may be missing), so the C# view still works. It has no CIL anchors:
/// those come only from REA's decoder.
/// </summary>
public static class ManagedListing
{
    public static DotNetModel Build(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DecompileFailure(DecompileFailureKind.Io, full + ": " + ex.Message, ex);
        }

        try
        {
            using var pe = new PEReader(ImmutableArray.Create(bytes));
            if (!pe.HasMetadata) throw new DecompileFailure(DecompileFailureKind.NotManaged, "no CLI metadata: " + full);
            var md = pe.GetMetadataReader();
            var info = Info(full, bytes, pe, md);
            var types = new List<DotNetType>();
            var sig = new SignatureNames(md);
            foreach (var th in md.TypeDefinitions)
            {
                ct.ThrowIfCancellationRequested();
                var t = md.GetTypeDefinition(th);
                var name = md.GetString(t.Name);
                var methods = new List<DotNetMethod>();
                var fullName = TypeFullName(md, th);
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var hasBody = m.RelativeVirtualAddress != 0;
                    int? ilSize = null;
                    if (hasBody)
                    {
                        try { ilSize = pe.GetMethodBody(m.RelativeVirtualAddress).Size; }
                        catch (BadImageFormatException) { hasBody = false; }
                    }
                    methods.Add(new DotNetMethod
                    {
                        Token = Token(MetadataTokens.GetToken(mh)),
                        Name = md.GetString(m.Name),
                        DeclaringType = fullName,
                        Signature = Signature(m, sig),
                        Flags = (int)m.Attributes,
                        HasBody = hasBody,
                        IlSize = ilSize,
                    });
                }
                // <Module> only matters when it holds global methods (C++/CLI, some obfuscators)
                if (name == "<Module>" && methods.Count == 0) continue;
                types.Add(new DotNetType
                {
                    Token = Token(MetadataTokens.GetToken(th)),
                    Namespace = md.GetString(t.Namespace),
                    Name = name,
                    FullName = fullName,
                    FieldCount = t.GetFields().Count,
                    Methods = methods,
                });
            }
            return new DotNetModel
            {
                Info = info,
                Types = types,
                MemberRefCount = md.MemberReferences.Count,
                MembersCoverage = "local",
                BuiltLocally = true,
            };
        }
        catch (BadImageFormatException ex)
        {
            throw new DecompileFailure(DecompileFailureKind.NotManaged, full + ": " + ex.Message, ex);
        }
    }

    private static string Token(int value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    private static DotNetAssemblyInfo Info(string full, byte[] bytes, PEReader pe, MetadataReader md)
    {
        string name = Path.GetFileNameWithoutExtension(full), version = "";
        string? tfm = null;
        var refs = md.AssemblyReferences.Select(h =>
        {
            var r = md.GetAssemblyReference(h);
            return new DotNetAssemblyRef(md.GetString(r.Name), r.Version.ToString());
        }).ToList();
        if (md.IsAssembly)
        {
            var def = md.GetAssemblyDefinition();
            name = md.GetString(def.Name);
            version = def.Version.ToString();
            foreach (var h in def.GetCustomAttributes())
            {
                var a = md.GetCustomAttribute(h);
                if (AttributeTypeName(md, a) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                var blob = md.GetBlobReader(a.Value);
                if (blob.Length >= 4 && blob.ReadUInt16() == 1) tfm = blob.ReadSerializedString();
            }
        }
        var cor = pe.PEHeaders.CorHeader;
        var machine = pe.PEHeaders.CoffHeader.Machine;
        var arch = machine switch
        {
            Machine.Amd64 => "x86_64",
            Machine.Arm64 => "arm64",
            Machine.I386 when cor is not null && (cor.Flags & CorFlags.ILOnly) != 0 && (cor.Flags & CorFlags.Requires32Bit) == 0 => "anycpu",
            Machine.I386 => "x86",
            _ => machine.ToString().ToLowerInvariant(),
        };
        return new DotNetAssemblyInfo
        {
            Name = name,
            Version = version,
            TargetFramework = tfm,
            Architecture = arch,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ByteLength = bytes.LongLength,
            Mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString(),
            MetadataVersion = md.MetadataVersion,
            CoverageState = "local",
            Confidence = "observed",
            References = refs,
            ResourceCount = md.ManifestResources.Count,
            AttributeCount = md.CustomAttributes.Count,
        };
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute a)
    {
        if (a.Constructor.Kind == HandleKind.MemberReference)
        {
            var parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)parent);
                return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            }
        }
        return "";
    }

    /// <summary>"Ns.Outer+Inner", the same shape the page already shows for REA's lists.</summary>
    private static string TypeFullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var name = md.GetString(t.Name);
        var outer = t.GetDeclaringType();
        if (!outer.IsNil) return TypeFullName(md, outer) + "+" + name;
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>"(int, string) → bool" — the arrow form REA's list uses, so both sources read alike.</summary>
    private static string Signature(MethodDefinition m, SignatureNames sig)
    {
        try
        {
            var s = m.DecodeSignature(sig, null);
            return "(" + string.Join(", ", s.ParameterTypes) + ") → " + s.ReturnType;
        }
        catch (BadImageFormatException) { return "(?)"; }
    }

    /// <summary>Short C#-style type names for signatures (no resolution of other assemblies).</summary>
    private sealed class SignatureNames(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "void", PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "sbyte", PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort", PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long", PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double", PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.IntPtr => "nint", PrimitiveTypeCode.UIntPtr => "nuint", PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            Strip(md.GetString(md.GetTypeDefinition(handle).Name));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            Strip(md.GetString(md.GetTypeReference(handle).Name));

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            md.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";
        public string GetGenericTypeParameter(object? genericContext, int index) => "T" + (index == 0 ? "" : index.ToString(CultureInfo.InvariantCulture));
        public string GetGenericMethodParameter(object? genericContext, int index) => "M" + (index == 0 ? "" : index.ToString(CultureInfo.InvariantCulture));
        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        /// <summary>"List`1" → "List".</summary>
        private static string Strip(string name)
        {
            var i = name.IndexOf('`');
            return i < 0 ? name : name[..i];
        }
    }
}
