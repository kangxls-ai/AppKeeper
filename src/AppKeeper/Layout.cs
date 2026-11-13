using System;
using System.Collections.Generic;
using System.Linq;

namespace AppKeeper;

internal enum DropAction { Before, After, Into }

internal static class Layout
{
    public static IEnumerable<Entry> Visible(Library library, bool inbox, string query) =>
        query.Length > 0
            ? library.Entries.Where(entry => Pinyin.Matches(entry.Name, query))
            : library.Entries.Where(entry => !entry.Group.HasValue && entry.Inbox == inbox);

    public static bool CanMove(Entry source, Entry target, DropAction action) =>
        source.Id != target.Id && (source.Kind != EntryKind.Group ||
            (action != DropAction.Into && !target.Group.HasValue && !target.Inbox));

    public static void Move(Library library, Entry source, Entry target, DropAction action)
    {
        if (!CanMove(source, target, action))
            return;
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
    }
}
