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

    // ── semantic actions ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferencesLocatesEveryBindingToTheNamedSymbol()
    {
        using var work = new TempWorkspace();
        work.Write("Lib.cs", """
            namespace N;
            public static class Lib
            {
                public static int Foo(int x) => x + 1;
            }
            """);
        work.Write("Use.cs", """
            namespace N;
            public class Caller
            {
                public int A() => Lib.Foo(1);
                public int B() => Lib.Foo(2);
            }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-references", symbol = "Lib.Foo" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        // Two call sites, on lines 4 and 5 of Use.cs.
        result.Content.Should().Contain("Use.cs:4:").And.Contain("Use.cs:5:").And.Contain("Foo");
    }

    [Fact]
    public async Task FindReferencesRejectsMissingSymbolArgument()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "class A {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-references" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
        result.Content.Should().Contain("symbol");
    }

    [Fact]
    public async Task FindDefinitionLocatesDeclarationSite()
    {
        using var work = new TempWorkspace();
        work.Write("Lib.cs", """
            namespace N;
            public class Service
            {
                public void Run() { }
            }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-definition", symbol = "Service.Run" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("Lib.cs:4:").And.Contain("Method").And.Contain("Run");
    }

    [Fact]
    public async Task FindCallersLocatesDirectCallSites()
    {
        using var work = new TempWorkspace();
        work.Write("Lib.cs", """
            namespace N;
            public static class Lib { public static void Run() { } }
            """);
        work.Write("A.cs", """
            namespace N;
            public class A { public void DoIt() { Lib.Run(); } }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-callers", symbol = "Lib.Run" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("depth 1").And.Contain("A.cs").And.Contain("DoIt").And.Contain("Lib.Run");
    }

    [Fact]
    public async Task FindCallersWalksBlastRadiusWithDepth()
    {
        using var work = new TempWorkspace();
        // Inner is called by Middle, which is called by Outer. depth=2 should surface both hops.
        work.Write("Inner.cs", """
            namespace N;
            public static class Inner { public static void Bottom() { } }
            """);
        work.Write("Middle.cs", """
            namespace N;
            public static class Middle { public static void Step() { Inner.Bottom(); } }
            """);
        work.Write("Outer.cs", """
            namespace N;
            public static class Outer { public static void Top() { Middle.Step(); } }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-callers", symbol = "Inner.Bottom", depth = 2 }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("depth 1").And.Contain("Middle.Step");
        result.Content.Should().Contain("depth 2").And.Contain("Outer.Top");
    }

    [Fact]
    public async Task FindCallersWithUnknownSymbolReturnsEmpty()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "namespace N; public class A {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-callers", symbol = "Nope.Missing" }),
            Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
    }

    [Fact]
    public async Task FindCallersRequiresSymbolArgument()
    {
        using var work = new TempWorkspace();
        work.Write("a.cs", "class A {}");

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "find-callers" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.InvalidInput);
    }

    [Fact]
    public async Task SemanticDiagnosticsReportsCompilerErrors()
    {
        using var work = new TempWorkspace();
        // Using an undefined type triggers a semantic (binding) error — not a parse error.
        work.Write("Broken.cs", """
            namespace N;
            public class C { Undefined x; }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "semantic-diagnostics" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Success);
        result.Content.Should().Contain("Broken.cs:").And.Contain("Error CS");
    }

    [Fact]
    public async Task SemanticDiagnosticsReturnsEmptyForCleanWorkspace()
    {
        using var work = new TempWorkspace();
        work.Write("Clean.cs", """
            namespace N;
            public class Clean { public int X => 42; }
            """);

        var tool = new CSharpAnalysisTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "semantic-diagnostics" }), Context(work.Root), CancellationToken.None);

        result.Kind.Should().Be(ToolResultKind.Empty);
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
