using System.Text.Json;
using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class InteractiveSetupTests
{
    [Fact]
    public void ParseProviderChoiceAcceptsTheNumericMenu()
    {
        InteractiveSetup.ParseProviderChoice("1").Should().Be("openai");
        InteractiveSetup.ParseProviderChoice("2").Should().Be("anthropic");
        InteractiveSetup.ParseProviderChoice("6").Should().Be("vertex");
    }

    [Fact]
    public void ParseProviderChoiceAcceptsLowercaseAndMixedCaseNames()
    {
        InteractiveSetup.ParseProviderChoice("anthropic").Should().Be("anthropic");
        InteractiveSetup.ParseProviderChoice("Anthropic").Should().Be("anthropic");
        InteractiveSetup.ParseProviderChoice("  OLLAMA  ").Should().Be("ollama");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7")]      // out of range
    [InlineData("0")]      // out of range
    [InlineData("nope")]   // unrecognized
    public void ParseProviderChoiceReturnsNullForUnrecognizedInput(string input)
    {
        InteractiveSetup.ParseProviderChoice(input).Should().BeNull();
    }

    [Fact]
    public void DescribeProviderReturnsOneLineSummary()
    {
        InteractiveSetup.DescribeProvider("anthropic").Should().Contain("Anthropic");
        InteractiveSetup.DescribeProvider("ollama").Should().Contain("Ollama");
        InteractiveSetup.DescribeProvider("bedrock").Should().Contain("AWS");
    }

    [Theory]
    [InlineData("openai", "apiKey", "baseUrl", "model")]
    [InlineData("anthropic", "apiKey", "baseUrl", "model")]
    [InlineData("ollama", "baseUrl", "model")]   // no API key for Ollama
    [InlineData("vertex", "baseUrl", "apiVersion", "model")] // no API key (ADC), but needs location
    public void QuestionsForReturnsTheExpectedSlots(string provider, params string[] expectedSlots)
    {
        var slots = InteractiveSetup.QuestionsFor(provider).Select(q => q.Slot).ToList();
        slots.Should().Contain(expectedSlots);
    }

    [Fact]
    public void QuestionsForMarksApiKeysAsSecret()
    {
        var anthropic = InteractiveSetup.QuestionsFor("anthropic");
        var apiKey = anthropic.Single(q => q.Slot == "apiKey");
        apiKey.Secret.Should().BeTrue();

        // Non-secret slots stay clear so they can be displayed in the prompt with defaults.
        anthropic.Single(q => q.Slot == "baseUrl").Secret.Should().BeFalse();
    }

    [Fact]
    public void QuestionsForReturnsEmptyForUnknownProvider()
    {
        InteractiveSetup.QuestionsFor("nope").Should().BeEmpty();
    }

    [Fact]
    public void MergeProviderSectionStartsFromEmptyConfigCleanly()
    {
        var json = InteractiveSetup.MergeProviderSection(
            existingJson: null,
            provider: "anthropic",
            answers: new Dictionary<string, string>
            {
                ["apiKey"] = "sk-ant-xyz",
                ["model"] = "claude-3-7-sonnet-latest",
            },
            setAsDefault: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("provider").GetString().Should().Be("anthropic");
        var ant = root.GetProperty("anthropic");
        ant.GetProperty("apiKey").GetString().Should().Be("sk-ant-xyz");
        ant.GetProperty("model").GetString().Should().Be("claude-3-7-sonnet-latest");
        ant.TryGetProperty("baseUrl", out _).Should().BeFalse(); // unset answers aren't written
    }

    [Fact]
    public void MergeProviderSectionPreservesUnrelatedProviderSections()
    {
        var existing = """
            {
              "provider": "openai",
              "openai":    { "apiKey": "keep-this", "model": "gpt-4o-mini" },
              "anthropic": { "apiKey": "old-ant" }
            }
            """;

        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: existing,
            provider: "anthropic",
            answers: new Dictionary<string, string> { ["apiKey"] = "new-ant-key" },
            setAsDefault: false);

        using var doc = JsonDocument.Parse(merged);
        var root = doc.RootElement;
        // openai section is untouched.
        root.GetProperty("openai").GetProperty("apiKey").GetString().Should().Be("keep-this");
        root.GetProperty("openai").GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        // anthropic section was replaced with the new answers.
        root.GetProperty("anthropic").GetProperty("apiKey").GetString().Should().Be("new-ant-key");
        // top-level default provider stays at the prior value because setAsDefault was false.
        root.GetProperty("provider").GetString().Should().Be("openai");
    }

    [Fact]
    public void MergeProviderSectionSetsTopLevelDefaultWhenRequested()
    {
        var existing = """{"provider":"openai","openai":{"apiKey":"x"}}""";

        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: existing,
            provider: "anthropic",
            answers: new Dictionary<string, string> { ["apiKey"] = "y" },
            setAsDefault: true);

        using var doc = JsonDocument.Parse(merged);
        doc.RootElement.GetProperty("provider").GetString().Should().Be("anthropic");
    }

    [Fact]
    public void MergeProviderSectionTreatsMalformedExistingJsonAsEmpty()
    {
        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: "{ this is not json",
            provider: "ollama",
            answers: new Dictionary<string, string> { ["baseUrl"] = "http://localhost:11434" },
            setAsDefault: true);

        using var doc = JsonDocument.Parse(merged);
        doc.RootElement.GetProperty("ollama").GetProperty("baseUrl").GetString().Should().Be("http://localhost:11434");
        doc.RootElement.GetProperty("provider").GetString().Should().Be("ollama");
    }

    [Fact]
    public void MergeProviderSectionDropsBlankAndWhitespaceAnswerValues()
    {
        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: null,
            provider: "openai",
            answers: new Dictionary<string, string>
            {
                ["apiKey"] = "sk-...",
                ["baseUrl"] = "",   // user pressed Enter with no default — should NOT be written
                ["model"] = "  ",   // whitespace-only — should NOT be written
            },
            setAsDefault: false);

        using var doc = JsonDocument.Parse(merged);
        var openai = doc.RootElement.GetProperty("openai");
        openai.GetProperty("apiKey").GetString().Should().Be("sk-...");
        openai.TryGetProperty("baseUrl", out _).Should().BeFalse();
        openai.TryGetProperty("model", out _).Should().BeFalse();
    }

    [Fact]
    public void ResolveDefaultPrefersEnvironmentFallbackOverExplicitDefault()
    {
        const string varName = "FREEAGENT_TEST_ENV_FALLBACK_VAR_XYZ";
        Environment.SetEnvironmentVariable(varName, "from-env");
        try
        {
            var q = new SetupQuestion("model", "Model", Default: "fallback-default", EnvFallback: varName);
            InteractiveSetup.ResolveDefault(q).Should().Be("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ResolveDefaultFallsBackToExplicitDefaultWhenEnvIsUnset()
    {
        var q = new SetupQuestion("model", "Model", Default: "fallback-default", EnvFallback: "FREEAGENT_DEFINITELY_NOT_SET_XYZ");
        InteractiveSetup.ResolveDefault(q).Should().Be("fallback-default");
    }
}
