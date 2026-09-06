using System.Text.RegularExpressions;

namespace ChopItUp.Core.Storage;

/// <summary>Room ids are slugs of the owner's title (M9 plan decision 13): they are MCP <c>room_id</c>
/// values, URL segments and, for a hub-created directory, folder names — so lowercase ASCII, capped,
/// unique, and never a Windows reserved device name.</summary>
public static class RoomIds
{
    public const int MaxChars = 40;
    private static readonly Regex NotSlug = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static string Slug(string name)
    {
        var s = NotSlug.Replace((name ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (s.Length > MaxChars) s = s[..MaxChars].TrimEnd('-');
        if (s.Length == 0) return "room";
        return Reserved.Contains(s) ? s + "-room" : s;
    }

    /// <summary>The slug, or the first of <c>slug-2</c>, <c>slug-3</c>, … that <paramref name="taken"/> does not claim.</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        var slug = Slug(name);
        if (!taken(slug)) return slug;
        for (int n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken(candidate)) return candidate;
        }
    }
}
