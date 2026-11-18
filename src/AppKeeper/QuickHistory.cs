using System;
using System.Collections.Generic;
using System.Linq;

namespace AppKeeper;

internal static class QuickHistory
{
    public const int SearchLimit = 10;
    public const int EntryLimit = 20;
    public const int QueryLimit = 256;

    public static void RecordSearch(Library data, string value)
    {
        var query = value.Trim();
        if (query.Length == 0 || query.Length > QueryLimit)
            return;
        MoveToFront(data.SearchHistory, query, SearchLimit, StringComparer.OrdinalIgnoreCase);
    }

    public static void RecordVisit(Library data, Guid id)
    {
        if (data.Entries.Any(entry => entry.Id == id))
            MoveToFront(data.RecentEntries, id, EntryLimit, EqualityComparer<Guid>.Default);
    }

    public static void RemoveSearch(Library data, string query) =>
        data.SearchHistory.RemoveAll(item => item.Equals(query, StringComparison.OrdinalIgnoreCase));

    public static void RemoveVisit(Library data, Guid id)
    {
        data.PinnedEntries.Remove(id);
        data.RecentEntries.Remove(id);
    }

    public static void ClearUnpinnedVisits(Library data)
    {
        var pinned = data.PinnedEntries.ToHashSet();
        data.RecentEntries.RemoveAll(id => !pinned.Contains(id));
    }

    private static void MoveToFront<T>(List<T> list, T value, int limit, IEqualityComparer<T> comparer)
    {
        list.RemoveAll(item => comparer.Equals(item, value));
        list.Insert(0, value);
        if (list.Count > limit)
            list.RemoveRange(limit, list.Count - limit);
    }

    public static IEnumerable<Entry> Entries(Library data)
    {
        var entries = data.Entries.ToDictionary(entry => entry.Id);
        return data.PinnedEntries.Concat(data.RecentEntries).Distinct()
            .Where(entries.ContainsKey).Take(EntryLimit).Select(id => entries[id]);
    }
}
