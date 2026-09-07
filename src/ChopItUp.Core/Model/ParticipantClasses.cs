namespace ChopItUp.Core.Model;

/// <summary>The roles a roster row can hold (grill ledger D5, owner ruling 2026-09-07: a SET, not
/// one value — `opus` is both the owner-visible builder and a judge). Row 11 stores and surfaces
/// these; row 19 enforces them (D8) and picks effort from them (D10).</summary>
public static class ParticipantClasses
{
    public const string Plumbing = "plumbing";
    public const string Visible = "visible";
    public const string Judge = "judge";
    public static readonly IReadOnlyList<string> All = [Plumbing, Visible, Judge];

    /// <summary>Splits, trims, lowercases, drops duplicates and anything outside the vocabulary, and
    /// preserves <see cref="All"/> order so two rows with the same set serialise identically. A value
    /// the owner mistyped by hand is dropped rather than thrown on: one bad cell must not stop every
    /// spawn in the hub, and <see cref="Unknown"/> gives the caller what to warn about.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        var tokens = Tokenize(stored);
        return All.Where(tokens.Contains).ToList();
    }

    /// <summary>The tokens Parse discarded, for a startup warning naming the row.</summary>
    public static IReadOnlyList<string> Unknown(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        var tokens = Tokenize(stored);
        return tokens.Where(t => !All.Contains(t)).Distinct().ToList();
    }

    public static bool Has(Participant p, string cls) => Parse(p.Classes).Contains(cls);

    private static HashSet<string> Tokenize(string stored) =>
        stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
}
