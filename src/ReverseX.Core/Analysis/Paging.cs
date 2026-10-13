namespace ReverseX.Core.Analysis;

/// <summary>Plain page arithmetic for the module list (pages are 0-based inside, shown 1-based).</summary>
public static class Paging
{
    public static int PageCount(int total, int pageSize) =>
        pageSize <= 0 || total <= 0 ? 1 : (total + pageSize - 1) / pageSize;

    public static int Clamp(int page, int total, int pageSize) =>
        Math.Clamp(page, 0, PageCount(total, pageSize) - 1);

    public static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> all, int page, int pageSize)
    {
        if (pageSize <= 0 || all.Count == 0) return Array.Empty<T>();
        page = Clamp(page, all.Count, pageSize);
        var start = page * pageSize;
        var take = Math.Min(pageSize, all.Count - start);
        var result = new T[take];
        for (var i = 0; i < take; i++) result[i] = all[start + i];
        return result;
    }
}
