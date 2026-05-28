using FluentAssertions;

namespace FreeAgent.Kernel.Tests.Commands;

public sealed class CommandRegistryTests
{
    [Fact]
    public void RegisterThenTryGetReturnsTheCommand()
    {
        var registry = new CommandRegistry();
        registry.Register(new("session.fork", "Fork session", "Branch the transcript."));

        registry.TryGet("session.fork")!.Label.Should().Be("Fork session");
    }

    [Fact]
    public void RegisterReplacesByIdLastWriteWins()
    {
        var registry = new CommandRegistry();
        registry.Register(new("a", "first"));
        registry.Register(new("a", "second"));

        registry.TryGet("a")!.Label.Should().Be("second");
        registry.All.Should().HaveCount(1);
    }

    [Fact]
    public void TryGetUnknownReturnsNull()
    {
        new CommandRegistry().TryGet("nope").Should().BeNull();
    }

    [Fact]
    public void AllSortsByCategoryThenLabel()
    {
        var registry = new CommandRegistry();
        registry.Register(new("z", "Zebra", Category: "Session"));
        registry.Register(new("a", "Alpha", Category: "Diagnostics"));
        registry.Register(new("b", "Beta", Category: "Diagnostics"));

        registry.All.Select(c => c.Label).Should().Equal("Alpha", "Beta", "Zebra");
    }

    [Fact]
    public void EmptyQueryReturnsAllInDefaultOrder()
    {
        var registry = new CommandRegistry();
        registry.Register(new("a", "Alpha", Category: "X"));
        registry.Register(new("b", "Beta", Category: "X"));

        registry.Search("").Should().HaveCount(2);
    }

    [Fact]
    public void SearchFiltersBySubsequenceMatchOnLabelOrId()
    {
        var registry = new CommandRegistry();
        registry.Register(new("session.fork", "Fork session"));
        registry.Register(new("session.tag", "Tag session"));
        registry.Register(new("plan.toggle", "Toggle plan mode"));

        var matches = registry.Search("frk").Select(c => c.Id).ToList();
        matches.Should().Contain("session.fork");
        matches.Should().NotContain("plan.toggle");
    }

    [Fact]
    public void SearchScoresTighterMatchesAhead()
    {
        var registry = new CommandRegistry();
        registry.Register(new("fork.long.id", "alpha"));   // "fk" requires walking 9 chars (f...k)
        registry.Register(new("fk", "beta"));              // "fk" is contiguous

        var ordered = registry.Search("fk").Select(c => c.Label).ToList();
        ordered[0].Should().Be("beta");
    }

    [Fact]
    public void SearchIsCaseInsensitive()
    {
        var registry = new CommandRegistry();
        registry.Register(new("Plan.Toggle", "Toggle plan mode"));

        registry.Search("toggle").Should().ContainSingle();
        registry.Search("TOGGLE").Should().ContainSingle();
    }

    [Fact]
    public void FuzzyScoreReturnsMaxWhenQueryNotASubsequence()
    {
        CommandRegistry.FuzzyScore("xyz", "hello").Should().Be(int.MaxValue);
    }

    [Fact]
    public void FuzzyScoreReturnsLastMinusFirstForSubsequenceMatch()
    {
        // "fk" in "fork": f@0, k@3 → score 3
        CommandRegistry.FuzzyScore("fk", "fork").Should().Be(3);
    }
}
