namespace RO2.Recruitments.Domain;

/// <summary>The page selection both boards share: poster GID descending, 10 rows a page, 1-based, clamped.</summary>
public static class Paginator
{
    /// <summary>Maximum number of rows per page; matches the client's result window of 10 row controls.</summary>
    public const int PageSize = 10;

    /// <summary>
    /// Orders rows by poster GID, descending, and returns the requested 1-based page. An out-of-range page returns
    /// the last page; an empty result still reports one page.
    /// </summary>
    public static (ushort Page, ushort Pages, List<T> Rows) Page<T>(
        IEnumerable<T> rows, Func<T, ulong> posterGid, ushort requestedPage)
    {
        var ordered = rows.OrderByDescending(posterGid).ToList();
        var pages = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
        pages = Math.Min(pages, ushort.MaxValue);
        var page = Math.Clamp((int)requestedPage, 1, pages);
        var slice = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        return ((ushort)page, (ushort)pages, slice);
    }
}
