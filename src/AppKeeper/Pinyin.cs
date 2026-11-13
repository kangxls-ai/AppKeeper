using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AppKeeper;

internal static class Pinyin
{
    private static readonly Lazy<Dictionary<int, string[]>> Dictionary = new(Load);

    private static Dictionary<int, string[]> Load()
    {
        using var reader = new StreamReader(Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("AppKeeper.Assets.pinyin.txt")!);
        var result = new Dictionary<int, string[]>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(' ');
            result[int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = parts[1..];
        }
        return result;
    }

    public static bool Matches(string name, string query)
    {
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        var needle = Normalize(query);
        if (needle.Length == 0)
            return false;

        var offsets = new HashSet<int>();
        var initialOffsets = new HashSet<int>();
        foreach (var rune in name.EnumerateRunes())
        {
            if (!Dictionary.Value.TryGetValue(rune.Value, out var syllables))
            {
                var literal = Normalize(rune.ToString());
                if (literal.Length == 0)
                    continue;
                syllables = [literal];
            }

            if (Advance(needle, syllables, offsets, initials: false) ||
                Advance(needle, syllables, initialOffsets, initials: true))
                return true;
        }
        return false;
    }

    private static bool Advance(string query, string[] syllables, HashSet<int> offsets, bool initials)
    {
        var next = new HashSet<int>();
        offsets.Add(0);
        foreach (var word in syllables)
        {
            var part = initials ? word.AsSpan(0, 1) : word.AsSpan();
            if (part.Contains(query.AsSpan(), StringComparison.Ordinal))
                return true;
            for (var start = 1; start < part.Length; start++)
            {
                if (query.AsSpan().StartsWith(part[start..], StringComparison.Ordinal))
                    next.Add(part.Length - start);
            }
            foreach (var offset in offsets)
            {
                var remaining = query.AsSpan(offset);
                if (part.StartsWith(remaining, StringComparison.Ordinal))
                    return true;
                if (remaining.StartsWith(part, StringComparison.Ordinal))
                    next.Add(offset + part.Length);
            }
        }
        offsets.Clear();
        offsets.UnionWith(next);
        return false;
    }

    private static string Normalize(string value) => string.Concat(value.ToLowerInvariant()
        .Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit));
}
