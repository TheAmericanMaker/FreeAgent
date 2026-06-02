using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FreeAgent.Kernel.Tests.Server;

/// <summary>
/// HTTP surface tests for FreeAgent.Server. Spins up the real WebApplication via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> — no network bind, in-process HTTP. We don't
/// exercise <c>POST /turns</c> end-to-end because that would call out to a real LLM provider; the
/// CRUD shape, listing, and the auth gate are fully covered.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class SessionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        // Make sure no env-set API key leaks into the open-mode tests.
        Environment.SetEnvironmentVariable("FREEAGENT_SERVER_API_KEY", null);
        _factory = factory;
    }

    [Fact]
    public async Task CreateSessionReturnsIdAndWorkingDirectory()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body.Should().NotBeNull();
        body!["sessionId"].ToString().Should().NotBeNullOrEmpty();
        body["workingDirectory"].ToString().Should().Be("/tmp");
    }

    [Fact]
    public async Task ListSessionsIncludesNewlyCreatedSession()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
        var created = await create.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var id = created!["sessionId"].ToString();

        var list = await client.GetFromJsonAsync<List<string>>("/sessions");

        list.Should().Contain(id!);
    }

    [Fact]
    public async Task GetSessionByIdReturnsStateSummary()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
        var id = (await create.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["sessionId"].ToString();

        var get = await client.GetAsync($"/sessions/{id}");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadAsStringAsync();
        body.Should().Contain("\"sessionId\"")
            // A freshly created session carries one message: the composed system prompt.
            .And.Contain("\"messageCount\":1")
            .And.Contain("\"planMode\":false");
    }

    [Fact]
    public async Task GetUnknownSessionReturns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/sessions/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteRemovesTheSession()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
        var id = (await create.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["sessionId"].ToString();

        var delete = await client.DeleteAsync($"/sessions/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deleteAgain = await client.DeleteAsync($"/sessions/{id}");
        deleteAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTurnOnUnknownSessionReturns404()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/sessions/nope/turns", new { userInput = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTurnWithEmptyInputReturns400()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
        var id = (await create.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["sessionId"].ToString();

        var response = await client.PostAsJsonAsync($"/sessions/{id}/turns", new { userInput = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OpenApiSpecExposesEverySessionEndpoint()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var spec = await response.Content.ReadAsStringAsync();
        spec.Should().Contain("\"openapi\":");
        spec.Should()
            .Contain("/sessions").And
            .Contain("/sessions/{id}").And
            .Contain("/sessions/{id}/turns");
    }

    [Fact]
    public async Task SessionCapReturns429WhenExceeded()
    {
        Environment.SetEnvironmentVariable("FREEAGENT_SERVER_MAX_SESSIONS", "1");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var client = factory.CreateClient();

            var first = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
            first.StatusCode.Should().Be(HttpStatusCode.Created);

            var second = await client.PostAsJsonAsync("/sessions", new { workingDirectory = "/tmp" });
            second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FREEAGENT_SERVER_MAX_SESSIONS", null);
        }
    }

    [Fact]
    public async Task ApiKeyGate_RejectsRequestsWithoutHeader()
    {
        Environment.SetEnvironmentVariable("FREEAGENT_SERVER_API_KEY", "test-key");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var client = factory.CreateClient();

            var response = await client.GetAsync("/sessions");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FREEAGENT_SERVER_API_KEY", null);
        }
    }

    [Fact]
    public async Task ApiKeyGate_AcceptsCorrectBearerToken()
    {
        Environment.SetEnvironmentVariable("FREEAGENT_SERVER_API_KEY", "test-key");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-key");

            var response = await client.GetAsync("/sessions");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FREEAGENT_SERVER_API_KEY", null);
        }
    }
}
