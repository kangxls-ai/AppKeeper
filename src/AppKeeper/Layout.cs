using System;
using System.Collections.Generic;
using System.Linq;

namespace AppKeeper;

internal enum DropAction { Before, After, Into }

internal static class Layout
{
    public static IEnumerable<Entry> Visible(Library library, bool inbox, string query, string? tag = null)
    {
        IEnumerable<Entry> entries = library.Entries;
        if (tag != null)
        {
            var members = library.Entries.Where(entry => entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
            var positions = library.TagOrders.TryGetValue(tag, out var order) ? order.Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index) : new Dictionary<Guid, int>();
            entries = members.OrderBy(entry => positions.GetValueOrDefault(entry.Id, int.MaxValue));
        }
        return query.Length > 0
            ? entries.Where(entry => Pinyin.Matches(entry.Name, query))
            : entries.Where(entry => (tag != null || !entry.Group.HasValue) && entry.Inbox == inbox);
    }

    public static bool IsTagName(string? tag) =>
        tag is { Length: > 0 and <= 24 } && tag == tag.Trim() && !tag.Any(char.IsControl);

    public static bool AssignTag(Library library, Entry entry, string? tag)
    {
        EnsureTagOrders(library);
        var canonical = library.Tags.Find(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (canonical == null || entry.Tags.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            return false;
        entry.Tags.Add(canonical);
        library.TagOrders[canonical].Add(entry.Id);
        return true;
    }

    // Capture the initial order for every tag before changing the global list. This also upgrades older backups.
    public static void EnsureTagOrders(Library library)
    {
        var previous = library.TagOrders ?? [];
        var orders = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in library.Tags)
        {
            var members = library.Entries.Where(entry => entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Select(entry => entry.Id).ToArray();
            var ids = members.ToHashSet();
            var saved = previous.Where(pair => pair.Key.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value ?? []).Where(ids.Contains).Distinct().ToList();
            var known = saved.ToHashSet();
            saved.AddRange(members.Where(id => !known.Contains(id)));
            orders[tag] = saved;
        }
        library.TagOrders = orders;
    }

    public static void UnassignTag(Library library, Entry entry, string tag)
    {
        EnsureTagOrders(library);
        entry.Tags.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (library.TagOrders.TryGetValue(tag, out var order)) order.Remove(entry.Id);
    }

    public static bool CanOrderInTag(Library library, string tag, Entry source, Entry? target) =>
        library.Entries.Contains(source) && source.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) &&
        (target == null || source.Id != target.Id && library.Entries.Contains(target) &&
            target.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    public static void OrderInTag(Library library, string tag, Entry source, Entry? target, bool after)
    {
        if (!CanOrderInTag(library, tag, source, target)) return;
        EnsureTagOrders(library);
        if (!library.TagOrders.TryGetValue(tag, out var order)) return;
        order.Remove(source.Id);
        var index = target == null ? order.Count : order.IndexOf(target.Id) + (after ? 1 : 0);
        order.Insert(index, source.Id);
    }

    public static bool MoveTag(Library library, string source, string target, bool after)
    {
        var tags = library.Tags;
        var from = tags.IndexOf(source);
        var to = tags.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return false;
        var destination = to + (after ? 1 : 0) - (from < to ? 1 : 0);
        if (destination == from)
            return false;
        tags.RemoveAt(from);
        tags.Insert(destination, source);
        return true;
    }

    public static void AddEntries(Library library, IEnumerable<Entry> entries, Guid? group, string? tag)
    {
        EnsureTagOrders(library);
        foreach (var entry in entries)
        {
            var existing = library.Entries.Find(item => item.Kind == entry.Kind &&
                string.Equals(item.Target, entry.Target, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                entry.Group = group;
                library.Entries.Add(entry);
            }
            else if (existing.Inbox || (group.HasValue && tag == null))
            {
                existing.Group = group;
                existing.Inbox = false;
            }
            AssignTag(library, existing ?? entry, tag);
        }
    }

    public static void RemoveTag(Library library, string tag)
    {
        EnsureTagOrders(library);
        library.Tags.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
        library.TagOrders.Remove(tag);
        foreach (var entry in library.Entries)
            entry.Tags.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanMove(Entry source, Entry target, DropAction action) =>
        source.Id != target.Id && (source.Kind != EntryKind.Group ||
            (action != DropAction.Into && !target.Group.HasValue && !target.Inbox));

    public static DropAction DropAt(Entry source, Entry target, double position, bool insideFolder)
    {
        if (!insideFolder && !source.Group.HasValue && source.Kind != EntryKind.Group &&
            position is >= .28 and <= .72)
            return DropAction.Into;
        return position < .5 ? DropAction.Before : DropAction.After;
    }

    public static void Move(Library library, Entry source, Entry target, DropAction action)
    {
        if (!CanMove(source, target, action))
            return;
        EnsureTagOrders(library);
        var entries = library.Entries;
        if (action == DropAction.Into)
        {
            if (target.Kind != EntryKind.Group)
            {
                if (target.Group.HasValue)
                    return;
                var group = new Entry { Kind = EntryKind.Group, Name = "新建文件夹" };
                entries.Insert(entries.IndexOf(target), group);
                target.Group = group.Id;
                target.Inbox = false;
                target = group;
            }
            source.Group = target.Id;
            source.Inbox = false;
            entries.Remove(source);
            entries.Add(source);
            return;
        }

        source.Group = target.Group;
        source.Inbox = target.Inbox;
        entries.Remove(source);
        entries.Insert(entries.IndexOf(target) + (action == DropAction.After ? 1 : 0), source);
    }

    public static void MoveHome(Library library, Entry entry)
    {
        EnsureTagOrders(library);
        library.Entries.Remove(entry);
        entry.Group = null;
        entry.Inbox = false;
        library.Entries.Add(entry);
    }

    public static void Remove(Library library, Entry entry)
    {
        if (entry.Kind == EntryKind.Group)
        {
            foreach (var child in library.Entries.Where(child => child.Group == entry.Id))
                child.Group = null;
        }
        else if (entry.Source != null || entry.Kind == EntryKind.File)
        {
            if (entry.Source != null)
                library.Ignored.Add(entry.Source);
            library.Ignored.Add("target:" + entry.Target);
        }
        library.Entries.Remove(entry);
        library.RecentEntries.Remove(entry.Id);
        library.PinnedEntries.Remove(entry.Id);
        foreach (var order in library.TagOrders.Values) order.Remove(entry.Id);
    }
}
