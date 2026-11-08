using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Eap.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:AgentsPath", CoreTests.AgentsRoot);
            b.UseSetting("Platform:ProviderOverride", "fake");
            b.UseSetting("Platform:EmbeddingProviderOverride", "fake"); // aucun appel réel, même si une clé est présente
        })
        .CreateClient();

    [Fact]
    public async Task Lists_agents_without_exposing_prompts()
    {
        var json = await Client().GetStringAsync("/agents");
        Assert.Contains("sav-produit", json);
        Assert.DoesNotContain("systemPrompt", json);
    }

    [Fact]
    public async Task Chat_returns_answer_usage_and_trace()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("X-User-Id", "test-user");

        var response = await client.PostAsJsonAsync("/agents/sav-produit/chat", new { message = "Quel est le prix de la bouilloire ?" });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.GetProperty("answer").GetString()!.Contains("39,90"), doc.RootElement.ToString());
        Assert.Equal("fake", doc.RootElement.GetProperty("usage").GetProperty("provider").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("traceId").GetString()));

        var usage = await client.GetStringAsync("/finops/usage");
        Assert.Contains("sav-produit", usage);
    }

    [Fact]
    public async Task Unknown_agent_returns_404()
    {
        var response = await Client().PostAsJsonAsync("/agents/inconnu/chat", new { message = "test" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
