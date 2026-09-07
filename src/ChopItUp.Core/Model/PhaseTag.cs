using System.Text.RegularExpressions;

namespace ChopItUp.Core.Model;

/// <summary>The hub's phase grammar (row 19, P3): the FIRST LINE of a conductor post, exactly
/// <c>phase: &lt;kind&gt;</c> or <c>phase: &lt;kind&gt;/&lt;name&gt;</c>, kind drawn from
/// <see cref="Kinds"/>. Trailing text on the same line is ignored on purpose (pass 1's M8): the first
/// draft required end-of-line after the tag, which rejected the most natural thing a conductor writes
/// (<c>phase: build @sonnet go</c>). A leading <c>**</c> or <c>#</c> is deliberately NOT tolerated —
/// the hub does not guess at markdown.</summary>
public sealed record PhaseTag(string Kind, string? Name)
{
    public static readonly IReadOnlyList<string> Kinds = ["plan", "build", "critique", "verify", "ping"];

    private static readonly Regex Pattern = new(
        @"^phase:[ \t]+(?<kind>[a-z]+)(?:/(?<name>[a-z0-9][a-z0-9-]{0,31}))?(?=[ \t]|\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses the first line of <paramref name="body"/> only — a tag on a later line never
    /// matches, and neither does a kind outside <see cref="Kinds"/>.</summary>
    public static bool TryParse(string body, out PhaseTag? tag)
    {
        tag = null;
        var match = Pattern.Match(FirstLine(body));
        if (!match.Success) return false;
        var kind = match.Groups["kind"].Value;
        if (!Kinds.Contains(kind)) return false;
        tag = new PhaseTag(kind, match.Groups["name"].Success ? match.Groups["name"].Value : null);
        return true;
    }

    /// <summary>Scans every line of <paramref name="body"/> for the first one that starts with
    /// <c>artifact:</c> (after leading whitespace) and returns the trimmed remainder, or null.</summary>
    public static string? Artifact(string body)
    {
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("artifact:", StringComparison.Ordinal))
                return trimmed["artifact:".Length..].Trim();
        }
        return null;
    }

    private static string FirstLine(string body)
    {
        var normalized = body.Replace("\r\n", "\n");
        var newline = normalized.IndexOf('\n');
        return newline < 0 ? normalized : normalized[..newline];
    }

    /// <summary>The exact value <c>run_phases</c> counts: <c>kind</c>, or <c>kind/name</c>.</summary>
    public override string ToString() => Name is null ? Kind : $"{Kind}/{Name}";
}
