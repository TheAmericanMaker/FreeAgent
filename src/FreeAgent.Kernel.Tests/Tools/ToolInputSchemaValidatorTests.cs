using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class ToolInputSchemaValidatorTests
{
    private static SchemaValidationResult Validate(string schema, string arguments) =>
        ToolInputSchemaValidator.Validate(JsonDocument.Parse(schema), JsonDocument.Parse(arguments));

    // A. Required missing property fails.
    [Fact]
    public void MissingRequiredPropertyIsInvalid()
    {
        var result = Validate(
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""",
            "{}");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("path");
        result.Error.Should().ContainEquivalentOf("required");
    }

    [Fact]
    public void PresentRequiredPropertyIsValid()
    {
        Validate(
            """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""",
            """{"path":"/tmp/x"}""")
            .IsValid.Should().BeTrue();
    }

    // B. Wrong primitive type fails.
    [Fact]
    public void WrongPrimitiveTypeIsInvalidAndMentionsPropertyAndType()
    {
        var result = Validate(
            """{"type":"object","properties":{"count":{"type":"integer"}}}""",
            """{"count":"not int"}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("count");
        result.Error.Should().ContainEquivalentOf("integer");
    }

    // C. Number accepts integer and floating point.
    [Fact]
    public void NumberAcceptsIntegerAndFloat()
    {
        const string schema = """{"type":"object","properties":{"x":{"type":"number"}}}""";

        Validate(schema, """{"x":1}""").IsValid.Should().BeTrue();
        Validate(schema, """{"x":1.5}""").IsValid.Should().BeTrue();
    }

    // D. Integer rejects floating point.
    [Fact]
    public void IntegerAcceptsWholeNumberAndRejectsFraction()
    {
        const string schema = """{"type":"object","properties":{"count":{"type":"integer"}}}""";

        Validate(schema, """{"count":1}""").IsValid.Should().BeTrue();
        Validate(schema, """{"count":1.5}""").IsValid.Should().BeFalse();
    }

    // E. Boolean / object / array primitive kinds validate correctly.
    [Fact]
    public void BooleanObjectAndArrayTypesValidate()
    {
        Validate("""{"properties":{"b":{"type":"boolean"}}}""", """{"b":true}""").IsValid.Should().BeTrue();
        Validate("""{"properties":{"b":{"type":"boolean"}}}""", """{"b":"x"}""").IsValid.Should().BeFalse();

        Validate("""{"properties":{"o":{"type":"object"}}}""", """{"o":{}}""").IsValid.Should().BeTrue();
        Validate("""{"properties":{"o":{"type":"object"}}}""", """{"o":[]}""").IsValid.Should().BeFalse();

        Validate("""{"properties":{"a":{"type":"array"}}}""", """{"a":[1,2]}""").IsValid.Should().BeTrue();
        Validate("""{"properties":{"a":{"type":"array"}}}""", """{"a":{}}""").IsValid.Should().BeFalse();

        Validate("""{"properties":{"s":{"type":"string"}}}""", """{"s":"hi"}""").IsValid.Should().BeTrue();
        Validate("""{"properties":{"s":{"type":"string"}}}""", """{"s":5}""").IsValid.Should().BeFalse();
    }

    // F. additionalProperties false rejects unknown properties.
    [Fact]
    public void AdditionalPropertiesFalseRejectsUnknownProperty()
    {
        var result = Validate(
            """{"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":false}""",
            """{"path":"x","extra":true}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("extra");
    }

    // G. additionalProperties omitted or true permits unknown properties.
    [Fact]
    public void AdditionalPropertiesOmittedOrTruePermitsUnknownProperty()
    {
        Validate("""{"type":"object","properties":{"path":{"type":"string"}}}""", """{"path":"x","extra":true}""")
            .IsValid.Should().BeTrue();
        Validate("""{"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":true}""", """{"path":"x","extra":true}""")
            .IsValid.Should().BeTrue();
    }

    // H. Empty schema {} allows object arguments.
    [Fact]
    public void EmptySchemaAllowsAnyObjectArguments()
    {
        Validate("{}", "{}").IsValid.Should().BeTrue();
        Validate("{}", """{"anything":1,"more":[true]}""").IsValid.Should().BeTrue();
    }

    [Fact]
    public void ObjectSchemaRejectsNonObjectArguments()
    {
        Validate("""{"type":"object","properties":{}}""", "[]").IsValid.Should().BeFalse();
    }

    // I. Malformed schema returns invalid without crashing.
    [Fact]
    public void MalformedSchemaRequiredNotArrayIsInvalid()
    {
        var result = Validate("""{"type":"object","required":"path"}""", "{}");

        result.IsValid.Should().BeFalse();
        result.Error.Should().ContainEquivalentOf("schema");
    }

    [Fact]
    public void MalformedSchemaPropertiesNotObjectIsInvalid()
    {
        var result = Validate("""{"type":"object","properties":[]}""", "{}");

        result.IsValid.Should().BeFalse();
        result.Error.Should().ContainEquivalentOf("schema");
    }

    [Fact]
    public void MalformedSchemaRootNotObjectIsInvalid()
    {
        var result = Validate("[]", "{}");

        result.IsValid.Should().BeFalse();
        result.Error.Should().ContainEquivalentOf("schema");
    }

    // Unknown/unsupported keywords are ignored, not fatal.
    [Fact]
    public void UnknownSchemaKeywordsAreIgnored()
    {
        Validate(
            """{"type":"object","title":"X","minProperties":1,"properties":{"path":{"type":"string","description":"a path"}}}""",
            """{"path":"x"}""")
            .IsValid.Should().BeTrue();
    }
}
