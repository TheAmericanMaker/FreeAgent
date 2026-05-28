using System.Text.Json;
using FluentAssertions;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Tools.Adapters;

public sealed class CSharpAnalysisToolTests
{
    private static JsonDocument Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static ToolContext Context(string workingDirectory) =>
        new(new SessionState("roslyn-session", workingDirectory, DateTimeOffset.UnixEpoch));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "freeagent-tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void FlagsAreReadOnlyAndConcurrencySafe()
    {
        var tool = new CSharpAnalysisTool();
        tool.Name.Should().Be("CSharpAnalysis");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsConcurrencySafe.Should().BeTrue();
        tool.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RequiredCapabilityIsFileReadCapOnResolvedRoot()
    {
        using var work = new TempWorkspace();
        var tool = new CSharpAnalysisTool();

        var caps = tool.RequiredCapabilities(Args(new { action = "list-types" }), Context(work.Root));

        caps.Should().HaveCount(1);
        caps[0].Should().BeOfType<FileReadCap>()
            .Which.Path.Should().Be(work.Root);
    }

    [Fact]
    public async Task ListTypesFindsClassesInterfacesRecordsAndEnumsWithNamespacePrefix()
    {
        using var work = new TempWorkspace();
        work.Write("Models.cs", """
            namespace Acme.Models;

            public class Order { }
            public interface IShippable { }
            public record Address(string Street);
            public record struct Point(int X, int Y);
            public enum OrderState { New, Paid }
            public delegate int Reducer(int acc, int next);
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-types" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should()
            .Contain("Models.cs:3: class Acme.Models.Order").And
            .Contain("Models.cs:4: interface Acme.Models.IShippable").And
            .Contain("Models.cs:5: record Acme.Models.Address").And
            .Contain("Models.cs:6: record struct Acme.Models.Point").And
            .Contain("Models.cs:7: enum Acme.Models.OrderState").And
            .Contain("Models.cs:8: delegate Acme.Models.Reducer");
    }

    [Fact]
    public async Task ListTypesHandlesNestedTypesUsingTheirEnclosingTypeChain()
    {
        using var work = new TempWorkspace();
        work.Write("Outer.cs", """
            namespace N;

            public class Outer
            {
                public class Inner { }
            }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-types" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("class N.Outer.Inner");
    }

    [Fact]
    public async Task ListMembersEnumeratesMethodsPropertiesFieldsAndCtors()
    {
        using var work = new TempWorkspace();
        work.Write("Service.cs", """
            namespace N;

            public class Service
            {
                public string Name { get; set; } = "";
                private int _count;
                public Service(string name) { Name = name; }
                public int Tally(int delta) => _count += delta;
            }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-members" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should()
            .Contain("property Service.Name").And
            .Contain("field Service._count").And
            .Contain("ctor Service.Service(string)").And
            .Contain("method Service.Tally(int)");
    }

    [Fact]
    public async Task DiagnosticsReportsParseErrors()
    {
        using var work = new TempWorkspace();
        // Missing closing brace produces a parse diagnostic
        work.Write("Broken.cs", "namespace N; public class Bad { public void Oops( { } ");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "diagnostics" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("Broken.cs:").And.Contain("Error CS");
    }

    [Fact]
    public async Task DiagnosticsReturnsEmptyForCleanFiles()
    {
        using var work = new TempWorkspace();
        work.Write("Clean.cs", "namespace N; public class Clean { }");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "diagnostics" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
    }

    [Fact]
    public async Task UnknownActionIsInvalidInput()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "class A {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "burn-it" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task GlobFilterRestrictsScanToMatchingFiles()
    {
        using var work = new TempWorkspace();
        work.Write("src/Keep.cs", "namespace N; public class Keep {}");
        work.Write("tests/Skip.cs", "namespace N; public class Skip {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-types", glob = "src/**/*.cs" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("Keep").And.NotContain("Skip");
    }

    [Fact]
    public async Task SingleFilePathScansOnlyThatFile()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "namespace N; public class A {}");
        work.Write("b.cs", "namespace N; public class B {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-types", path = "a.cs" }), Context(work.Root), CancellationToken.None);

        result.Content.Should().Contain("class N.A").And.NotContain("class N.B");
    }

    [Fact]
    public async Task NoCSharpFilesReturnsEmpty()
    {
        using var work = new TempWorkspace();
        work.Write("notes.txt", "hello");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(Args(new { action = "list-types" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
    }
}
