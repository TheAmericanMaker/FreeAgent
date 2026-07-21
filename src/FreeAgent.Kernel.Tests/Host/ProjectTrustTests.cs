using FluentAssertions;
using FreeAgent.Host;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests.Host;

public sealed class ProjectTrustTests
{
    [Fact]
    public void DescribeRequestsListsEveryExecutableAndGrantSurface()
    {
        var config = PermissionConfig.Parse("""
            {
              "hooks": { "sessionStart": [ { "run": "echo hi" } ], "preToolUse": [ { "run": "echo pre" } ] },
              "mcp": { "servers": [ { "name": "fs", "command": "mcp-fs" } ] },
              "lsp": { "servers": [ { "name": "csharp", "languageId": "csharp", "fileExtensions": [".cs"], "command": "csharp-ls", "args": [] } ] },
              "allow": [ { "capability": "FileWriteCap" } ]
            }
            """);

        var requests = ProjectTrust.DescribeRequests(config);

        requests.Should().HaveCount(4);
        requests.Should().Contain(r => r.Contains("hook"));
        requests.Should().Contain(r => r.Contains("MCP") && r.Contains("fs"));
        requests.Should().Contain(r => r.Contains("LSP") && r.Contains("csharp"));
        requests.Should().Contain(r => r.Contains("allow-rule"));
    }

    [Fact]
    public void DescribeRequestsIsEmptyForADenyOnlyConfig()
    {
        var config = PermissionConfig.Parse("""
            { "deny": [ { "capability": "ProcessExecCap", "pattern": "rm" } ], "denyTools": ["WriteFile"] }
            """);

        ProjectTrust.DescribeRequests(config).Should().BeEmpty();
    }

    [Fact]
    public void TrustRoundTripsThroughTheStoreAndNormalizesTrailingSeparator()
    {
        var store = Path.Combine(Path.GetTempPath(), $"freeagent-trust-{Guid.NewGuid():N}.json");
        const string dir = "/tmp/freeagent/project";
        try
        {
            ProjectTrust.IsTrusted(dir, store).Should().BeFalse();

            ProjectTrust.Trust(dir, store);

            ProjectTrust.IsTrusted(dir, store).Should().BeTrue();
            // A trailing separator must not defeat the match.
            ProjectTrust.IsTrusted(dir + "/", store).Should().BeTrue();
            // An untrusted sibling stays untrusted.
            ProjectTrust.IsTrusted("/tmp/freeagent/other", store).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(store)) File.Delete(store);
        }
    }

    [Fact]
    public void MissingStoreMeansNothingIsTrusted()
    {
        var store = Path.Combine(Path.GetTempPath(), $"freeagent-trust-absent-{Guid.NewGuid():N}.json");
        ProjectTrust.IsTrusted("/tmp/anything", store).Should().BeFalse();
    }
}
