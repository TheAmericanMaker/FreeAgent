using FluentAssertions;

namespace FreeAgent.Kernel.Tests.Providers;

public sealed class ModelCatalogTests
{
    [Fact]
    public void RegisterThenResolveReturnsTheModel()
    {
        var catalog = new ModelCatalog();
        var model = new Model("claude-x", "anthropic", ContextTokens: 100_000, SupportsThinking: true);

        catalog.Register(model);

        catalog.TryResolve("anthropic", "claude-x").Should().BeSameAs(model);
    }

    [Fact]
    public void ResolveIsScopedByWireApi()
    {
        var catalog = new ModelCatalog();
        catalog.Register(new Model("shared-name", "anthropic"));
        catalog.Register(new Model("shared-name", "openai"));

        catalog.TryResolve("anthropic", "shared-name")!.WireApi.Should().Be("anthropic");
        catalog.TryResolve("openai", "shared-name")!.WireApi.Should().Be("openai");
    }

    [Fact]
    public void ReRegisterReplacesPriorEntry()
    {
        var catalog = new ModelCatalog();
        catalog.Register(new Model("m", "anthropic", ContextTokens: 1));
        catalog.Register(new Model("m", "anthropic", ContextTokens: 999));

        catalog.TryResolve("anthropic", "m")!.ContextTokens.Should().Be(999);
        catalog.All.Should().HaveCount(1);
    }

    [Fact]
    public void UnknownReturnsNull()
    {
        var catalog = new ModelCatalog();
        catalog.TryResolve("anthropic", "nope").Should().BeNull();
    }

    [Fact]
    public void DefaultsRegistersKnownAnthropicAndOpenAIModels()
    {
        var catalog = ModelCatalog.Defaults();

        catalog.TryResolve("anthropic", "claude-3-7-sonnet-latest")!.SupportsThinking.Should().BeTrue();
        catalog.TryResolve("openai", "gpt-4o")!.SupportsVision.Should().BeTrue();
        catalog.TryResolve("openai", "gpt-4o-mini")!.ContextTokens.Should().Be(128_000);
    }
}
