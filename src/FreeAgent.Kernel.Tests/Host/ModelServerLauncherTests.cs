using FluentAssertions;
using FreeAgent.Host;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class ModelServerLauncherTests
{
    [Fact]
    public void ResolveSourceToUrl_PassesPlainHttpsThrough()
    {
        ModelServerLauncher.ResolveSourceToUrl("https://example.com/m.gguf")
            .Should().Be("https://example.com/m.gguf");
    }

    [Fact]
    public void ResolveSourceToUrl_ExpandsHfShorthandToResolveMainUrl()
    {
        ModelServerLauncher.ResolveSourceToUrl("hf:Qwen/Qwen2.5-Coder-7B-Instruct-GGUF/qwen2.5-coder-7b-instruct-q4_k_m.gguf")
            .Should().Be("https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/qwen2.5-coder-7b-instruct-q4_k_m.gguf");
    }

    [Fact]
    public void ResolveSourceToUrl_HandlesNestedPathSegments()
    {
        ModelServerLauncher.ResolveSourceToUrl("hf:owner/repo/sub/dir/file.gguf")
            .Should().Be("https://huggingface.co/owner/repo/resolve/main/sub/dir/file.gguf");
    }

    [Theory]
    [InlineData("hf:")]
    [InlineData("hf:onlyowner")]
    [InlineData("hf:owner/repo")]
    [InlineData("hf:owner/repo/")]
    public void ResolveSourceToUrl_RejectsMalformedHfSpec(string spec)
    {
        Action act = () => ModelServerLauncher.ResolveSourceToUrl(spec);
        act.Should().Throw<ArgumentException>().WithMessage("*hf:*");
    }

    [Fact]
    public void ResolveModelName_AbsolutePathPassesThrough()
    {
        // /etc/hosts exists on Linux and is harmless to point at — we never open it, just resolve.
        if (!File.Exists("/etc/hosts")) return;
        ModelServerLauncher.ResolveModelName("/etc/hosts").Should().Be("/etc/hosts");
    }

    [Fact]
    public void ResolveModelName_UnresolvableNameReturnsTheInput()
    {
        // No file in the catalog matches this; the caller's "file not found" surfaces instead.
        ModelServerLauncher.ResolveModelName("definitely-not-a-real-model-name").Should().Be("definitely-not-a-real-model-name");
    }

    [Fact]
    public void ResolveModelName_FindsBareNameInCatalogWithAutoExtension()
    {
        Directory.CreateDirectory(ModelServerLauncher.ModelsDir());
        var fakeModel = Path.Combine(ModelServerLauncher.ModelsDir(), "freeagent-test.gguf");
        File.WriteAllBytes(fakeModel, []);
        try
        {
            ModelServerLauncher.ResolveModelName("freeagent-test").Should().Be(fakeModel);
            ModelServerLauncher.ResolveModelName("freeagent-test.gguf").Should().Be(fakeModel);
        }
        finally
        {
            File.Delete(fakeModel);
        }
    }

    [Fact]
    public void ListCatalog_OnFreshInstallReportsEmpty()
    {
        // We can't realistically guarantee the cache dir is empty (other tests may have added
        // entries), so this asserts only the headline string when the dir is missing entirely.
        // The fake-model test above covers the non-empty path.
        var path = ModelServerLauncher.ModelsDir();
        if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.gguf").Any()) return;

        ModelServerLauncher.ListCatalog().Should().Contain("No models downloaded");
    }
}
