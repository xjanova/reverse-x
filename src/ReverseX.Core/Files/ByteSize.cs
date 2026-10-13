using System.Globalization;

namespace ReverseX.Core.Files;

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? $"{v.ToString("0", CultureInfo.InvariantCulture)} B"
            : $"{v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[i]}";
    }
}
