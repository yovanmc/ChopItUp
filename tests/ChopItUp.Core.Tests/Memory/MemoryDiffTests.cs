using ChopItUp.Core.Memory;

namespace ChopItUp.Core.Tests.Memory;

public sealed class MemoryDiffTests
{
    public static IEnumerable<object[]> LineDiffs() =>
    [
        // identical inputs report no change
        ["a\nb\nc\n", "a\nb\nc\n",
            new[] { (DiffOp.Same, "a"), (DiffOp.Same, "b"), (DiffOp.Same, "c"), (DiffOp.Same, "") }],
        // a single altered line is a deletion then an addition in place
        ["a\nb\nc\n", "a\nB\nc\n",
            new[] { (DiffOp.Same, "a"), (DiffOp.Del, "b"), (DiffOp.Add, "B"), (DiffOp.Same, "c"), (DiffOp.Same, "") }],
        // an insertion at the head is located there
        ["a\nb\n", "x\na\nb\n",
            new[] { (DiffOp.Add, "x"), (DiffOp.Same, "a"), (DiffOp.Same, "b"), (DiffOp.Same, "") }],
        // a deletion at the tail is located there
        ["a\nb\nc\n", "a\nb\n",
            new[] { (DiffOp.Same, "a"), (DiffOp.Same, "b"), (DiffOp.Del, "c"), (DiffOp.Same, "") }],
    ];

    [Theory]
    [MemberData(nameof(LineDiffs))]
    public void Compute_reports_no_change_for_identical_inputs_and_locates_each_change_where_it_happened(
        string before, string after, (DiffOp, string)[] expected)
    {
        var diff = MemoryDiff.Compute(before, after);
        Assert.Equal(expected, diff.Select(l => (l.Op, l.Text)));
    }

    [Fact]
    public void Crlf_and_lf_inputs_that_only_differ_in_line_ending_report_no_change()
    {
        var diff = MemoryDiff.Compute("a\r\nb\r\n", "a\nb\n");
        Assert.All(diff, l => Assert.Equal(DiffOp.Same, l.Op));
    }

    [Fact]
    public void Hunks_collapses_a_20_line_unchanged_run_to_one_skip_with_3_lines_of_context_each_side()
    {
        var before = string.Concat(Enumerable.Range(1, 20).Select(i => $"line{i}\n")) + "old\n";
        var after = string.Concat(Enumerable.Range(1, 20).Select(i => $"line{i}\n")) + "new\n";
        var hunks = MemoryDiff.Hunks(MemoryDiff.Compute(before, after));

        // 3 lines of context, a skip, 3 more lines of context, then the change, then the trailing empty line.
        Assert.Equal(DiffOp.Same, hunks[0].Op);
        Assert.Equal("line1", hunks[0].Text);
        Assert.Equal("line3", hunks[2].Text);
        Assert.Equal(DiffOp.Skip, hunks[3].Op);
        Assert.Contains("14 unchanged lines", hunks[3].Text);   // 20 - 2*3
        Assert.Equal("line18", hunks[4].Text);
        Assert.Equal(DiffOp.Same, hunks[4].Op);
        Assert.Equal("line20", hunks[6].Text);
        Assert.Equal((DiffOp.Del, "old"), (hunks[7].Op, hunks[7].Text));
        Assert.Equal((DiffOp.Add, "new"), (hunks[8].Op, hunks[8].Text));
    }

    [Fact]
    public void Hunks_leaves_a_run_no_longer_than_twice_the_context_untouched()
    {
        // 5 "lineN" rows plus the trailing empty line from the final '\n' = a 6-line Same run, exactly
        // 2*context (3) — at the boundary, so it must NOT collapse.
        var before = "old\n" + string.Concat(Enumerable.Range(1, 5).Select(i => $"line{i}\n"));
        var after = "new\n" + string.Concat(Enumerable.Range(1, 5).Select(i => $"line{i}\n"));
        var hunks = MemoryDiff.Hunks(MemoryDiff.Compute(before, after));
        Assert.DoesNotContain(hunks, l => l.Op == DiffOp.Skip);
        Assert.Equal(8, hunks.Count);   // del + add + 6 unchanged lines (line1..line5, trailing empty)
    }

    [Fact]
    public void Oversized_input_is_cut_and_the_cut_is_reported()
    {
        var before = string.Concat(Enumerable.Range(1, MemoryDiff.MaxLines + 50).Select(i => $"b{i}\n"));
        var after = string.Concat(Enumerable.Range(1, MemoryDiff.MaxLines + 50).Select(i => $"a{i}\n"));
        var diff = MemoryDiff.Compute(before, after);
        Assert.Equal(DiffOp.Skip, diff[^1].Op);
        Assert.Contains(MemoryDiff.MaxLines.ToString(), diff[^1].Text);
        Assert.DoesNotContain(diff, l => l.Op == DiffOp.Same && (l.Text == $"b{MemoryDiff.MaxLines + 10}" || l.Text == $"a{MemoryDiff.MaxLines + 10}"));
    }
}
