using System.Text.RegularExpressions;

namespace ChopItUp.Core.Model;

/// <summary>A reference to a file in a room's artifacts folder: <c>artifact:&lt;relative path&gt;</c>.
/// Windows resolves several spellings to one file (a trailing dot or space, an empty or <c>.</c>
/// segment, an 8.3 short name). Authorship is looked up by spelling, so each of those is refused
/// here: an alias would otherwise let an author judge its own file.</summary>
public static partial class ArtifactRef
{
    public const string Prefix = "artifact:";

    private static readonly char[] Forbidden = [':', '*', '?', '"', '<', '>', '|'];

    [GeneratedRegex(@"~\d", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameMarker();

    public static bool IsReference(string value) => value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path inside the artifacts folder, with forward slashes, when
    /// <paramref name="value"/> is a reference with one canonical spelling.</summary>
    public static bool TryRelative(string value, out string relative)
    {
        relative = "";
        if (!IsReference(value)) return false;
        var path = value[Prefix.Length..].Replace('\\', '/');
        if (!IsCanonicalRelative(path)) return false;
        relative = path;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is a relative path with one canonical spelling. A
    /// backslash counts as a separator, so <c>a\..\x</c> holds a <c>..</c> segment.</summary>
    public static bool IsCanonicalRelative(string path) =>
        path.Replace('\\', '/').Split('/').All(IsCanonicalSegment);

    private static bool IsCanonicalSegment(string segment) =>
        segment.Length > 0 && segment is not ("." or "..")
        && !segment.EndsWith('.') && !segment.EndsWith(' ')
        && segment.IndexOfAny(Forbidden) < 0 && !segment.Any(char.IsControl)
        && !ShortNameMarker().IsMatch(segment);
}
