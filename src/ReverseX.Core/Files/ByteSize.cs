using System.Globalization;

namespace ReverseX.Core.Files;

public static class ByteSize
{
    /// <summary>A no-break space, so a wrapping line never leaves the unit alone at the start of the next line.</summary>
    public const char Gap = ' ';

    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? $"{v.ToString("0", CultureInfo.InvariantCulture)}{Gap}B"
            : $"{v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)}{Gap}{units[i]}";
    }
}
