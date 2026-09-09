namespace ChopItUp.Core.Memory;

public enum DiffOp { Same, Add, Del, Skip }

public sealed record DiffLine(DiffOp Op, string Text);

/// <summary>Row 23 (item 3, ticket 05): a line-level comparison of a topic's current text against what a
/// <c>rewrite</c> proposal would write, computed by the hub so the owner reviews a diff rather than a
/// wall of text. Ordinal, case-sensitive; <c>\r\n</c> is normalised to <c>\n</c> first so a line-ending
/// change alone never reads as an edit. Pure and static: no store, no disk.</summary>
public static class MemoryDiff
{
    /// <summary>An LCS table is O(n*m); an input longer than this is cut before computing one, and the cut
    /// is reported as a trailing <see cref="DiffOp.Skip"/> line rather than silently dropped.</summary>
    public const int MaxLines = 1_200;

    /// <summary>Line-by-line LCS. At a divergence a deletion is emitted before an addition (so a pure
    /// replacement reads as "old line gone, new line here" rather than the reverse).</summary>
    public static IReadOnlyList<DiffLine> Compute(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var truncated = false;
        if (a.Count > MaxLines) { a = a.Take(MaxLines).ToList(); truncated = true; }
        if (b.Count > MaxLines) { b = b.Take(MaxLines).ToList(); truncated = true; }

        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { result.Add(new DiffLine(DiffOp.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add(new DiffLine(DiffOp.Del, a[x])); x++; }
            else { result.Add(new DiffLine(DiffOp.Add, b[y])); y++; }
        }
        while (x < n) { result.Add(new DiffLine(DiffOp.Del, a[x])); x++; }
        while (y < m) { result.Add(new DiffLine(DiffOp.Add, b[y])); y++; }

        if (truncated) result.Add(new DiffLine(DiffOp.Skip, $"… input cut at {MaxLines} lines …"));
        return result;
    }

    /// <summary>Collapses any run of <see cref="DiffOp.Same"/> longer than <c>2 * context</c> lines into
    /// one <see cref="DiffOp.Skip"/> line naming how many lines it elides, keeping <paramref name="context"/>
    /// lines of untouched context on each side of a change — the shape a unified diff renders.</summary>
    public static IReadOnlyList<DiffLine> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var result = new List<DiffLine>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Op != DiffOp.Same) { result.Add(lines[i]); i++; continue; }
            var start = i;
            while (i < lines.Count && lines[i].Op == DiffOp.Same) i++;
            var run = i - start;
            if (run <= 2 * context)
            {
                for (var k = start; k < i; k++) result.Add(lines[k]);
            }
            else
            {
                for (var k = start; k < start + context; k++) result.Add(lines[k]);
                result.Add(new DiffLine(DiffOp.Skip, $"… {run - 2 * context} unchanged lines …"));
                for (var k = i - context; k < i; k++) result.Add(lines[k]);
            }
        }
        return result;
    }

    private static List<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();
}
