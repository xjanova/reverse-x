using System.Buffers.Binary;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Minimal resources.arsc reader: resolves simple (non-bag) resource values such as the app label string.</summary>
public sealed class ArscReader
{
    private readonly byte[] _d;
    private readonly ResStringPool? _global;
    // resource id → (prefers default config) value chunk offset
    private readonly Dictionary<uint, (int Offset, bool Default)> _entries = new();

    public ArscReader(byte[] data)
    {
        _d = data;
        if (data.Length < 12 || U16(0) != 0x0002) return;
        var pos = (int)U16(2);
        while (pos + 8 <= data.Length)
        {
            var type = U16(pos);
            var size = (int)U32(pos + 4);
            if (size < 8 || pos + size > data.Length) break;
            if (type == 0x0001 && _global is null) _global = new ResStringPool(data, pos);
            else if (type == 0x0200) ReadPackage(pos, size);
            pos += size;
        }
    }

    private ushort U16(int at) => at + 2 <= _d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(at)) : (ushort)0;
    private uint U32(int at) => at + 4 <= _d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(at)) : 0;

    private void ReadPackage(int start, int size)
    {
        var pkgId = U32(start + 8);
        var headerSize = U16(start + 2);
        var pos = start + headerSize;
        var end = start + size;
        while (pos + 8 <= end)
        {
            var type = U16(pos);
            var hs = U16(pos + 2);
            var sz = (int)U32(pos + 4);
            if (sz < 8 || pos + sz > end) break;
            if (type == 0x0201) ReadType(pkgId, pos, hs, sz);
            pos += sz;
        }
    }

    private void ReadType(uint pkgId, int start, int headerSize, int size)
    {
        var typeId = _d[start + 8];
        var flags = _d[start + 9];
        var entryCount = (int)U32(start + 12);
        var entriesStart = (int)U32(start + 16);
        // ResTable_config begins at +20; language bytes at config+8. Zero language = default config.
        var isDefault = start + 30 <= _d.Length && _d[start + 28] == 0 && _d[start + 29] == 0;
        var sparse = (flags & 0x01) != 0;
        var off16 = (flags & 0x02) != 0;
        var idx = start + headerSize;
        for (var i = 0; i < entryCount; i++)
        {
            int entryIndex, offset;
            if (sparse)
            {
                entryIndex = U16(idx + i * 4);
                offset = U16(idx + i * 4 + 2) * 4;
            }
            else if (off16)
            {
                entryIndex = i;
                var o = U16(idx + i * 2);
                if (o == 0xFFFF) continue;
                offset = o * 4;
            }
            else
            {
                entryIndex = i;
                var o = U32(idx + i * 4);
                if (o == 0xFFFFFFFF) continue;
                offset = (int)o;
            }
            var at = start + entriesStart + offset;
            if (at + 8 > start + size) continue;
            var id = pkgId << 24 | (uint)typeId << 16 | (uint)entryIndex;
            if (!_entries.TryGetValue(id, out var cur) || (!cur.Default && isDefault))
                _entries[id] = (at, isDefault);
        }
    }

    /// <summary>Text for a simple value (string, int, bool, reference chain up to 4 deep). Null when unknown or a bag.</summary>
    public string? Resolve(uint id, int depth = 0)
    {
        if (depth > 4 || !_entries.TryGetValue(id, out var e)) return null;
        var at = e.Offset;
        var flags = U16(at + 2);
        byte dataType;
        uint data;
        if ((flags & 0x0008) != 0)
        {
            // compact entry: key(u16) flags(u16, high byte = type) data(u32)
            dataType = (byte)(flags >> 8);
            data = U32(at + 4);
        }
        else
        {
            if ((flags & 0x0001) != 0) return null; // complex (bag)
            var vs = at + U16(at);
            dataType = _d[vs + 3];
            data = U32(vs + 4);
        }
        return dataType switch
        {
            0x03 => _global?.Get(data),
            0x01 => Resolve(data, depth + 1),
            0x10 => ((int)data).ToString(System.Globalization.CultureInfo.InvariantCulture),
            0x12 => data != 0 ? "true" : "false",
            _ => null,
        };
    }
}
