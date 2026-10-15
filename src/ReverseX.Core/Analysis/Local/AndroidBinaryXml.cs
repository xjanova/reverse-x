using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Android ResStringPool (used by binary XML and resources.arsc).</summary>
internal sealed class ResStringPool
{
    private readonly byte[] _d;
    private readonly int _start;
    private readonly int _count;
    private readonly int _stringsStart;
    private readonly bool _utf8;
    private readonly string?[] _cache;

    /// <param name="offset">Start of the chunk (its type field).</param>
    public ResStringPool(byte[] data, int offset)
    {
        _d = data;
        _start = offset;
        _count = (int)U32(offset + 8);
        var flags = U32(offset + 16);
        _stringsStart = offset + (int)U32(offset + 20);
        _utf8 = (flags & 0x100) != 0;
        if (_count < 0 || _count > 5_000_000) _count = 0;
        _cache = new string?[_count];
    }

    public int Count => _count;

    private uint U32(int at) => at + 4 <= _d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(at)) : 0;
    private ushort U16(int at) => at + 2 <= _d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(at)) : (ushort)0;

    public string Get(long index)
    {
        if (index < 0 || index >= _count) return "";
        if (_cache[index] is { } c) return c;
        var s = "";
        try
        {
            var p = _stringsStart + (int)U32(_start + 28 + (int)index * 4);
            if (_utf8)
            {
                // utf16 length (1-2 bytes), then utf8 byte length (1-2 bytes), then bytes.
                p += (_d[p] & 0x80) != 0 ? 2 : 1;
                int len = _d[p];
                if ((len & 0x80) != 0) { len = (len & 0x7F) << 8 | _d[p + 1]; p += 2; } else p += 1;
                len = Math.Min(len, _d.Length - p);
                s = Encoding.UTF8.GetString(_d, p, len);
            }
            else
            {
                int len = U16(p);
                if ((len & 0x8000) != 0) { len = (len & 0x7FFF) << 16 | U16(p + 2); p += 4; } else p += 2;
                len = Math.Min(len, (_d.Length - p) / 2);
                s = Encoding.Unicode.GetString(_d, p, len * 2);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { s = ""; }
        return _cache[index] = s;
    }
}

/// <summary>Decodes Android's compiled (binary) XML, e.g. AndroidManifest.xml inside an APK, into an XDocument.</summary>
public static class AndroidBinaryXml
{
    public static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";

    // Attribute resource ids, for manifests whose attribute names were stripped by obfuscators.
    private static readonly Dictionary<uint, string> KnownAttrIds = new()
    {
        [0x01010001] = "label", [0x01010002] = "icon", [0x01010003] = "name", [0x01010006] = "permission",
        [0x0101000f] = "debuggable", [0x01010010] = "exported", [0x0101000e] = "enabled", [0x01010018] = "authorities",
        [0x01010020] = "protectionLevel", [0x01010024] = "value", [0x01010025] = "resource",
        [0x01010026] = "mimeType", [0x01010027] = "scheme", [0x01010028] = "host", [0x01010029] = "port", [0x0101002a] = "path",
        [0x0101002b] = "pathPrefix", [0x0101002c] = "pathPattern", [0x0101020c] = "minSdkVersion", [0x0101021b] = "versionCode",
        [0x0101021c] = "versionName", [0x01010270] = "targetSdkVersion", [0x01010280] = "allowBackup",
        [0x010104ec] = "usesCleartextTraffic", [0x01010527] = "networkSecurityConfig", [0x01010572] = "compileSdkVersion",
        [0x01010573] = "compileSdkVersionCodename", [0x01010000] = "theme", [0x0101001d] = "readPermission", [0x0101001e] = "writePermission",
        [0x0101001b] = "grantUriPermissions", [0x01010011] = "process", [0x0101001f] = "multiprocess", [0x01010281] = "required",
        [0x0101026c] = "maxSdkVersion", [0x01010276] = "extractNativeLibs", [0x010103af] = "largeHeap",
    };

    /// <param name="resolve">Turns a resource reference (0x7f…) into text when possible (e.g. via resources.arsc).</param>
    public static XDocument Decode(byte[] d, Func<uint, string?>? resolve = null)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(d) != 0x0003)
            throw new InvalidDataException("not Android binary XML");
        ResStringPool? pool = null;
        uint[] resIds = Array.Empty<uint>();
        var nsPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        XElement? root = null;
        var stack = new Stack<XElement>();
        var p = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        var end = Math.Min(d.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));
        if (end <= 8) end = d.Length;
        int pos = p;
        while (pos + 8 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 2));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
            if (size < 8 || pos + size > d.Length) break;
            switch (type)
            {
                case 0x0001:
                    pool = new ResStringPool(d, pos);
                    break;
                case 0x0180:
                    var n = (size - headerSize) / 4;
                    resIds = new uint[n];
                    for (var i = 0; i < n; i++) resIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + headerSize + i * 4));
                    break;
                case 0x0100 when pool is not null:
                    {
                        var prefix = pool.Get(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 16)));
                        var uri = pool.Get(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 20)));
                        if (uri.Length > 0) nsPrefixes[uri] = prefix;
                        break;
                    }
                case 0x0102 when pool is not null:
                    {
                        var nameIdx = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 20));
                        var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 24));
                        var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 26));
                        var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 28));
                        var name = SafeName(pool.Get(nameIdx), "element");
                        var el = new XElement(name);
                        var a0 = pos + 16 + attrStart;
                        for (var i = 0; i < attrCount; i++)
                        {
                            var a = a0 + i * Math.Max((int)attrSize, 20);
                            if (a + 20 > d.Length) break;
                            var nsIdx = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(a));
                            var anIdx = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(a + 4));
                            var rawIdx = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(a + 8));
                            var dataType = d[a + 15];
                            var data = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(a + 16));
                            var an = pool.Get(anIdx);
                            if ((an.Length == 0 || !IsXmlName(an)) && anIdx >= 0 && anIdx < resIds.Length && KnownAttrIds.TryGetValue(resIds[anIdx], out var known))
                                an = known;
                            var ns = nsIdx >= 0 ? pool.Get(nsIdx) : "";
                            if (ns.Length == 0 && anIdx >= 0 && anIdx < resIds.Length && (resIds[anIdx] >> 24) == 0x01) ns = AndroidNs.NamespaceName;
                            var value = rawIdx >= 0 ? pool.Get(rawIdx) : FormatValue(dataType, data, pool, resolve);
                            XName xn = ns.Length > 0 ? XNamespace.Get(ns) + SafeName(an, "attr" + i) : SafeName(an, "attr" + i);
                            if (el.Attribute(xn) is null) el.SetAttributeValue(xn, value);
                        }
                        if (stack.Count == 0)
                        {
                            root ??= el;
                            if (!ReferenceEquals(root, el)) root.Add(el);
                        }
                        else stack.Peek().Add(el);
                        stack.Push(el);
                        break;
                    }
                case 0x0103:
                    if (stack.Count > 0) stack.Pop();
                    break;
            }
            pos += size;
        }
        root ??= new XElement("manifest");
        foreach (var (uri, prefix) in nsPrefixes)
            if (prefix.Length > 0 && IsXmlName(prefix)) root.SetAttributeValue(XNamespace.Xmlns + prefix, uri);
        if (root.GetNamespaceOfPrefix("android") is null && root.Descendants().Concat(new[] { root }).Any(e => e.Attributes().Any(x => x.Name.Namespace == AndroidNs)))
            root.SetAttributeValue(XNamespace.Xmlns + "android", AndroidNs.NamespaceName);
        return new XDocument(root);
    }

    private static string FormatValue(byte type, uint data, ResStringPool pool, Func<uint, string?>? resolve) => type switch
    {
        0x03 => pool.Get(data),
        0x10 => ((int)data).ToString(CultureInfo.InvariantCulture),
        0x11 => "0x" + data.ToString("x", CultureInfo.InvariantCulture),
        0x12 => data != 0 ? "true" : "false",
        0x01 => resolve?.Invoke(data) ?? "@0x" + data.ToString("x8", CultureInfo.InvariantCulture),
        0x02 => "?0x" + data.ToString("x8", CultureInfo.InvariantCulture),
        0x04 => BitConverter.Int32BitsToSingle((int)data).ToString(CultureInfo.InvariantCulture),
        >= 0x1c and <= 0x1f => "#" + data.ToString("x8", CultureInfo.InvariantCulture),
        0x00 => "",
        _ => "0x" + data.ToString("x", CultureInfo.InvariantCulture),
    };

    private static bool IsXmlName(string s)
    {
        if (s.Length == 0) return false;
        try { XmlNameCheck(s); return true; } catch (System.Xml.XmlException) { return false; }
    }

    private static void XmlNameCheck(string s) => System.Xml.XmlConvert.VerifyNCName(s);

    private static string SafeName(string s, string fallback) => IsXmlName(s) ? s : fallback;
}
