using System.Text.RegularExpressions;

namespace SwiftGet.Core;

/// <summary>
/// Expands batch addresses: "file[1-20].zip", "img[001-100].jpg" (zero padding), "part[a-f].rar",
/// or "file*.zip" with a separate from/to range. Several [..] groups are combined.
/// </summary>
public static partial class BatchPattern
{
    public const int MaxLinks = 10000;

    [GeneratedRegex(@"\[(\w+)-(\w+)\]")]
    private static partial Regex RangeRegex();

    public static bool HasPattern(string text) => RangeRegex().IsMatch(text) || text.Contains('*');

    public static List<string> Expand(string pattern, string? from = null, string? to = null)
    {
        // "*" form: replaced by the from/to range
        if (pattern.Contains('*') && !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
            pattern = pattern.Replace("*", $"[{from.Trim()}-{to.Trim()}]");

        var results = new List<string> { pattern };
        while (true)
        {
            var next = new List<string>();
            bool any = false;
            foreach (var s in results)
            {
                var m = RangeRegex().Match(s);
                if (!m.Success) { next.Add(s); continue; }
                any = true;
                foreach (var value in Range(m.Groups[1].Value, m.Groups[2].Value))
                {
                    next.Add(s[..m.Index] + value + s[(m.Index + m.Length)..]);
                    if (next.Count > MaxLinks) throw new InvalidOperationException(Loc.F("BatchTooMany", MaxLinks));
                }
            }
            results = next;
            if (!any) return results;
        }
    }

    static IEnumerable<string> Range(string a, string b)
    {
        if (long.TryParse(a, out var x) && long.TryParse(b, out var y))
        {
            // "001-100" keeps three digits
            int width = a.Length > 1 && a.StartsWith('0') ? a.Length : 0;
            long step = x <= y ? 1 : -1;
            for (long i = x; step > 0 ? i <= y : i >= y; i += step)
                yield return width > 0 ? i.ToString().PadLeft(width, '0') : i.ToString();
        }
        else if (a.Length == 1 && b.Length == 1 && char.IsLetter(a[0]) && char.IsLetter(b[0]))
        {
            int step = a[0] <= b[0] ? 1 : -1;
            for (char c = a[0]; step > 0 ? c <= b[0] : c >= b[0]; c = (char)(c + step))
                yield return c.ToString();
        }
        else throw new FormatException(Loc.F("BatchBadRange", $"{a}-{b}"));
    }
}
