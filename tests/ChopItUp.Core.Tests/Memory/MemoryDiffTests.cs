using ChopItUp.Core.Memory;

namespace ChopItUp.Core.Tests.Memory;

public sealed class MemoryDiffTests
{
    [Fact]
    public void Identical_inputs_report_no_change()
    {
        var text = "a\nb\nc\n";
        var diff = MemoryDiff.Compute(text, text);
        Assert.All(diff, l => Assert.Equal(DiffOp.Same, l.Op));
        Assert.Equal(new[] { "a", "b", "c", "" }, diff.Select(l => l.Text));
    }

    [Fact]
    public void A_single_altered_line_is_a_deletion_then_an_addition_in_place()
    {
        var before = "a\nb\nc\n";
        var after = "a\nB\nc\n";
        var diff = MemoryDiff.Compute(before, after);
        Assert.Equal(
            new[] { (DiffOp.Same, "a"), (DiffOp.Del, "b"), (DiffOp.Add, "B"), (DiffOp.Same, "c"), (DiffOp.Same, "") },
            diff.Select(l => (l.Op, l.Text)));
    }

    [Fact]
    public void An_insertion_at_the_head_is_located_there()
    {
        var before = "a\nb\n";
        var after = "x\na\nb\n";
        var diff = MemoryDiff.Compute(before, after);
        Assert.Equal(
            new[] { (DiffOp.Add, "x"), (DiffOp.Same, "a"), (DiffOp.Same, "b"), (DiffOp.Same, "") },
            diff.Select(l => (l.Op, l.Text)));
    }

    [Fact]
    public void A_deletion_at_the_tail_is_located_there()
    {
        var before = "a\nb\nc\n";
        var after = "a\nb\n";
        var diff = MemoryDiff.Compute(before, after);
        Assert.Equal(
            new[] { (DiffOp.Same, "a"), (DiffOp.Same, "b"), (DiffOp.Del, "c"), (DiffOp.Same, "") },
            diff.Select(l => (l.Op, l.Text)));
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
