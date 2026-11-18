using System.Text.RegularExpressions;

namespace ChopItUp.Core.Model;

/// <summary>One <c>artifact:</c> line of a conductor post. <see cref="Spaced"/> is whether white space
/// followed the colon: <c>artifact:x.png</c> is the spelling a reference is easily mistaken for.</summary>
public sealed record ArtifactLine(string Value, bool Spaced);

/// <summary>The hub's phase grammar: the FIRST LINE of a conductor post, exactly
/// <c>phase: &lt;kind&gt;</c> or <c>phase: &lt;kind&gt;/&lt;name&gt;</c>, kind drawn from
/// <see cref="Kinds"/>. Trailing text on the same line is ignored on purpose: requiring end-of-line
/// after the tag would reject the most natural thing a conductor writes
/// (<c>phase: build @sonnet go</c>). A leading <c>**</c> or <c>#</c> is deliberately NOT tolerated:
/// the hub does not guess at markdown.</summary>
public sealed record PhaseTag(string Kind, string? Name)
{
    public static readonly IReadOnlyList<string> Kinds = ["plan", "build", "critique", "verify", "ping"];

    private static readonly Regex Pattern = new(
        @"^phase:[ \t]+(?<kind>[a-z]+)(?:/(?<name>[a-z0-9][a-z0-9-]{0,31}))?(?=[ \t]|\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses the first line of <paramref name="body"/> only — a tag on a later line never
    /// matches, and neither does a kind outside <see cref="Kinds"/>.</summary>
    public static bool TryParse(string body, out PhaseTag? tag) => TryParse(body, out tag, out _);

    /// <summary>Same as the two-argument overload, and also reports <paramref name="prefixLength"/>:
    /// the length of the matched tag token on the (CRLF-normalised) first line, 0 when there is no
    /// tag. Mention parsing uses this to find where the tag ends and the recipient region begins.</summary>
    public static bool TryParse(string body, out PhaseTag? tag, out int prefixLength)
    {
        tag = null;
        prefixLength = 0;
        var match = Pattern.Match(FirstLine(body));
        if (!match.Success) return false;
        var kind = match.Groups["kind"].Value;
        if (!Kinds.Contains(kind)) return false;
        tag = new PhaseTag(kind, match.Groups["name"].Success ? match.Groups["name"].Value : null);
        prefixLength = match.Length;
        return true;
    }

    /// <summary>Every line of <paramref name="body"/> that starts with <c>artifact:</c> (after leading
    /// whitespace), in order, each with its trimmed remainder.</summary>
    public static IReadOnlyList<ArtifactLine> Artifacts(string body)
    {
        var lines = new List<ArtifactLine>();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("artifact:", StringComparison.Ordinal)) continue;
            var rest = trimmed["artifact:".Length..];
            lines.Add(new ArtifactLine(rest.Trim(), rest.Length > 0 && char.IsWhiteSpace(rest[0])));
        }
        return lines;
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
