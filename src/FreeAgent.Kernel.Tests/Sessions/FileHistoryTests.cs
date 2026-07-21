using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Sessions;

public sealed class FileHistoryTests
{
    [Fact]
    public void EmptyHistoryPopsNothing()
    {
        var history = new FileHistory();

        history.Count.Should().Be(0);
        history.TryPop(out _).Should().BeFalse();
    }

    [Fact]
    public void RecordsAreReturnedInLifoOrder()
    {
        var history = new FileHistory();
        history.Record("/a.txt", null);
        history.Record("/b.txt", "v1");
        history.Record("/c.txt", "v2");

        history.Count.Should().Be(3);
        history.TryPop(out var top).Should().BeTrue();
        top.Path.Should().Be("/c.txt");
        top.PreviousContent.Should().Be("v2");

        history.TryPop(out var next).Should().BeTrue();
        next.Path.Should().Be("/b.txt");

        history.TryPop(out var last).Should().BeTrue();
        last.Path.Should().Be("/a.txt");
        last.PreviousContent.Should().BeNull(); // file didn't exist before

        history.TryPop(out _).Should().BeFalse();
    }
}
