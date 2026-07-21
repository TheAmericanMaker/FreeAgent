using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Diff;

public sealed class ColoredDiffTests
{
    [Fact]
    public void IdenticalTextRendersEmptyDiff()
    {
        ColoredDiff.Render("hello\nworld\n", "hello\nworld\n").Should().BeEmpty();
    }

    [Fact]
    public void DiffEmitsHeaderRemoveAndAddLines()
    {
        var output = ColoredDiff.Render("a\nb\nc\n", "a\nB\nc\n", oldLabel: "Lib.cs", newLabel: "Lib.cs", color: false);

        output.Should().Contain("--- Lib.cs").And.Contain("+++ Lib.cs");
        output.Should().Contain("-b").And.Contain("+B");
        output.Should().Contain("@@");
    }

    [Fact]
    public void ColorEnabledEmitsAnsiSequences()
    {
        var output = ColoredDiff.Render("a\nb\n", "a\nB\n", color: true);
        output.Should().Contain(ColoredDiff.Ansi.Red).And.Contain(ColoredDiff.Ansi.Green).And.Contain(ColoredDiff.Ansi.Cyan);
    }

    [Fact]
    public void ColorDisabledEmitsPlainText()
    {
        var output = ColoredDiff.Render("a\nb\n", "a\nB\n", color: false);
        output.Should().NotContain("[31m").And.NotContain("[32m");
    }

    [Fact]
    public void HunkHeaderUsesOneBasedLineNumbers()
    {
        var output = ColoredDiff.Render(
            "1\n2\n3\n4\n5\nold\n7\n8\n9\n10\n",
            "1\n2\n3\n4\n5\nNEW\n7\n8\n9\n10\n",
            color: false);

        // The change is on line 6; with 3 lines of context the hunk header should report line 3.
        output.Should().Contain("@@ -3,7 +3,7 @@");
    }

    [Fact]
    public void ContextLinesAreLimitedByConfiguration()
    {
        // contextLines=0 should produce no leading or trailing equal lines.
        var output = ColoredDiff.Render(
            "before\nold\nafter\n",
            "before\nNEW\nafter\n",
            color: false,
            contextLines: 0);

        var changeLines = output.Split('\n').Where(l => l.StartsWith("-") || l.StartsWith("+")).ToList();
        changeLines.Where(l => l.StartsWith("---") || l.StartsWith("+++")).Should().HaveCount(2); // headers
        changeLines.Where(l => l.StartsWith("-") && !l.StartsWith("---")).Should().ContainSingle().Which.Should().Be("-old");
        changeLines.Where(l => l.StartsWith("+") && !l.StartsWith("+++")).Should().ContainSingle().Which.Should().Be("+NEW");
    }

    [Fact]
    public void HandlesAdditionToEmptyFile()
    {
        var output = ColoredDiff.Render("", "first\nsecond\n", color: false);
        output.Should().Contain("+first").And.Contain("+second");
    }

    [Fact]
    public void HandlesDeletionToEmptyFile()
    {
        var output = ColoredDiff.Render("first\nsecond\n", "", color: false);
        output.Should().Contain("-first").And.Contain("-second");
    }

    [Fact]
    public void NormalizesCrlfLineEndings()
    {
        // CRLF in old, LF in new — should not register as a diff if content is otherwise equal.
        ColoredDiff.Render("a\r\nb\r\n", "a\nb\n").Should().BeEmpty();
    }
}
