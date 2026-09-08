using System.Text.RegularExpressions;
namespace ChopItUp.Core.Memory;

/// <summary>Row 18 (L5, L6): review hints computed once when a proposal is created and stored
/// comma-joined. They flag, never block — the owner decides (decision 6).</summary>
public static class ProposalFlags
{
    public const string InstructionLike = "instruction-like";
    public const string Fence = "fence";
    public const string FromDirectory = "from-directory";
    private static readonly Regex Imperative = new(
        @"^\s*(?:[-*]\s+)?(?:always|never|you must|you should|you are|ignore|disregard|do not|don't|from now on|run|execute|delete|remove|post|call|send|install|override|forget)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FenceLine = new(@"^\s*--- (?:begin memory|end memory|end skill)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Flags in a fixed order, or null when there is nothing to flag.</summary>
    public static string? Compute(string body, bool fromDirectory)
    {
        var lines = (body ?? "").Replace("\r\n", "\n").Split('\n');
        var flags = new List<string>();
        if (lines.Any(l => Imperative.IsMatch(l))) flags.Add(InstructionLike);
        if (lines.Any(l => FenceLine.IsMatch(l))) flags.Add(Fence);
        if (fromDirectory) flags.Add(FromDirectory);
        return flags.Count == 0 ? null : string.Join(',', flags);
    }

    public static IReadOnlyList<string> Parse(string? flags) =>
        string.IsNullOrWhiteSpace(flags) ? [] : flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
