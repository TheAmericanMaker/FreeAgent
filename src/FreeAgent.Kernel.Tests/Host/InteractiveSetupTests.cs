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
        // anthropic section was updated with the new answers.
        root.GetProperty("anthropic").GetProperty("apiKey").GetString().Should().Be("new-ant-key");
        // top-level default provider stays at the prior value because setAsDefault was false.
        root.GetProperty("provider").GetString().Should().Be("openai");
    }

    [Fact]
    public void MergeProviderSectionPreservesExistingValuesWhenAnswersArePartial()
    {
        var existing = """
            {
              "provider": "anthropic",
              "anthropic": {
                "apiKey": "keep-secret",
                "baseUrl": "https://api.anthropic.com",
                "model": "old-model"
              }
            }
            """;

        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: existing,
            provider: "anthropic",
            answers: new Dictionary<string, string> { ["model"] = "new-model" },
            setAsDefault: true);

        using var doc = JsonDocument.Parse(merged);
        var anthropic = doc.RootElement.GetProperty("anthropic");
        anthropic.GetProperty("apiKey").GetString().Should().Be("keep-secret");
        anthropic.GetProperty("baseUrl").GetString().Should().Be("https://api.anthropic.com");
        anthropic.GetProperty("model").GetString().Should().Be("new-model");
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
    public void MergeProviderSectionDoesNotLetBlankAnswersEraseExistingValues()
    {
        var existing = """{"openai":{"apiKey":"sk-old","model":"gpt-4o"}}""";

        var merged = InteractiveSetup.MergeProviderSection(
            existingJson: existing,
            provider: "openai",
            answers: new Dictionary<string, string>
            {
                ["apiKey"] = "",
                ["model"] = "  ",
            },
            setAsDefault: false);

        using var doc = JsonDocument.Parse(merged);
        var openai = doc.RootElement.GetProperty("openai");
        openai.GetProperty("apiKey").GetString().Should().Be("sk-old");
        openai.GetProperty("model").GetString().Should().Be("gpt-4o");
    }

    [Fact]
    public void ExistingProviderValueReadsProviderSectionSlots()
    {
        var existing = """{"vertex":{"baseUrl":"my-project","apiVersion":"europe-west1"}}""";

        InteractiveSetup.ExistingProviderValue(existing, "vertex", "baseUrl").Should().Be("my-project");
        InteractiveSetup.ExistingProviderValue(existing, "vertex", "apiVersion").Should().Be("europe-west1");
        InteractiveSetup.ExistingProviderValue(existing, "vertex", "model").Should().BeNull();
    }

    [Fact]
    public void ExistingProviderValueFallsBackToOpenAiLegacyFlatFields()
    {
        var existing = """{"apiKey":"sk-legacy","baseUrl":"https://gateway.example/v1","model":"custom"}""";

        InteractiveSetup.ExistingProviderValue(existing, "openai", "apiKey").Should().Be("sk-legacy");
        InteractiveSetup.ExistingProviderValue(existing, "openai", "baseUrl").Should().Be("https://gateway.example/v1");
        InteractiveSetup.ExistingProviderValue(existing, "openai", "model").Should().Be("custom");
        InteractiveSetup.ExistingProviderValue(existing, "anthropic", "apiKey").Should().BeNull();
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
    public void ResolveDefaultUsesExistingValueBeforeExplicitDefault()
    {
        var q = new SetupQuestion("model", "Model", Default: "fallback-default");
        InteractiveSetup.ResolveDefault(q, existingValue: "from-existing").Should().Be("from-existing");
    }

    [Fact]
    public void ResolveDefaultFallsBackToExplicitDefaultWhenEnvIsUnset()
    {
        var q = new SetupQuestion("model", "Model", Default: "fallback-default", EnvFallback: "FREEAGENT_DEFINITELY_NOT_SET_XYZ");
        InteractiveSetup.ResolveDefault(q).Should().Be("fallback-default");
    }
}
