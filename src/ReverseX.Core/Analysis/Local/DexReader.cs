using System.Buffers.Binary;
using System.Text;

namespace ReverseX.Core.Analysis.Local;

/// <summary>Reads class names and the string pool from a Dalvik .dex file (header + id tables only; no bytecode decoding).</summary>
public static class DexReader
{
    public sealed record DexInfo(string Version, List<string> Classes, int StringCount);

    public static bool IsDex(ReadOnlySpan<byte> d) => d.Length >= 0x70 && d[0] == (byte)'d' && d[1] == (byte)'e' && d[2] == (byte)'x' && d[3] == (byte)'\n';

    /// <param name="onString">Receives every string in the pool (for URL / secret scanning). May be null.</param>
    public static DexInfo Read(byte[] d, Action<string>? onString, CancellationToken ct)
    {
        if (!IsDex(d)) throw new InvalidDataException("not a dex file");
        var version = Encoding.ASCII.GetString(d, 4, 3);
        var stringIdsSize = (int)U32(d, 0x38);
        var stringIdsOff = (int)U32(d, 0x3C);
        var typeIdsSize = (int)U32(d, 0x40);
        var typeIdsOff = (int)U32(d, 0x44);
        var classDefsSize = (int)U32(d, 0x60);
        var classDefsOff = (int)U32(d, 0x64);
        if (!InRange(d, stringIdsOff, stringIdsSize, 4) || !InRange(d, typeIdsOff, typeIdsSize, 4) || !InRange(d, classDefsOff, classDefsSize, 32))
            throw new InvalidDataException("dex tables out of range");

        string Str(int idx)
        {
            if ((uint)idx >= (uint)stringIdsSize) return "";
            var off = (int)U32(d, stringIdsOff + idx * 4);
            if (off <= 0 || off >= d.Length) return "";
            // uleb128 utf16 length, then MUTF-8 bytes until 0.
            var p = off;
            while (p < d.Length && (d[p] & 0x80) != 0) p++;
            p++;
            var e = p;
            while (e < d.Length && d[e] != 0) e++;
            return Encoding.UTF8.GetString(d, p, e - p);
        }

        if (onString is not null)
            for (var i = 0; i < stringIdsSize; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var s = Str(i);
                if (s.Length >= 10) onString(s);
            }

        var classes = new List<string>(classDefsSize);
        for (var i = 0; i < classDefsSize; i++)
        {
            var typeIdx = (int)U32(d, classDefsOff + i * 32);
            if ((uint)typeIdx >= (uint)typeIdsSize) continue;
            var desc = Str((int)U32(d, typeIdsOff + typeIdx * 4));
            classes.Add(DescriptorToName(desc));
        }
        return new DexInfo(version, classes, stringIdsSize);
    }

    public static string DescriptorToName(string desc) =>
        desc.Length > 2 && desc[0] == 'L' && desc[^1] == ';' ? desc[1..^1].Replace('/', '.') : desc;

    private static uint U32(byte[] d, int at) => at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : 0;

    private static bool InRange(byte[] d, int off, int count, int item) =>
        count == 0 || (off > 0 && count > 0 && (long)off + (long)count * item <= d.Length);
}
