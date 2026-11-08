using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Knowledge;
using Eap.Core.Models;

namespace Eap.Tests;

public class CoreTests
{
    internal static readonly string AgentsRoot = FindAgents();

    private static string FindAgents()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "agents"))) return Path.Combine(dir.FullName, "agents");
        throw new DirectoryNotFoundException("agents");
    }

    private static (AgentRunner Runner, InMemoryUsageLedger Ledger, AgentManifest Sav) CreateRunner()
    {
        var registry = AgentRegistry.LoadFrom(AgentsRoot);
        var sav = registry.Find("sav-produit")!;
        var providers = new Dictionary<string, ProviderOptions>
        {
            ["fake"] = new() { Prices = { ["fake-model"] = new ModelPrice { InputPerMillion = 1m, OutputPerMillion = 2m } } },
        };
        var ledger = new InMemoryUsageLedger();
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        var runner = new AgentRunner(new ChatClientFactory(providers), ledger, _ => kb,
            modelOverride: new ModelOverride("fake", "fake-model"));
        return (runner, ledger, sav);
    }

    [Fact]
    public void Registry_loads_all_sample_agents()
    {
        var registry = AgentRegistry.LoadFrom(AgentsRoot);
        Assert.Contains(registry.All, a => a.Id == "sav-produit");
        Assert.Contains(registry.All, a => a.Id == "conformite-promo");
        Assert.All(registry.All, a => Assert.False(string.IsNullOrWhiteSpace(a.SystemPrompt)));
    }

    [Fact]
    public void Governance_rejects_production_agent_without_evaluation()
    {
        var m = new AgentManifest
        {
            Id = "x", Name = "X", Purpose = "p", Status = AgentStatus.Production,
            Owners = new() { Business = "b", Tech = "t" },
        };
        Assert.Contains(AgentRegistry.Validate(m), e => e.Contains("évaluation"));
    }

    [Fact]
    public void Price_is_computed_per_million_tokens()
    {
        var price = new ModelPrice { InputPerMillion = 0.30m, OutputPerMillion = 2.50m };
        Assert.Equal(0.00055m, price.Cost(1000, 100));
    }

    [Theory]
    [InlineData("Combien de temps pour retourner un produit ?", "Retours et remboursement")]
    [InlineData("Quelle est l'autonomie de l'aspirateur ?", "Aspirateur sans fil Kerlenn Cyclone (réf. AS-CY2)")]
    [InlineData("La garantie de la cafetière pro ?", "Cafetière à filtre Kerlenn Pro 12 tasses (réf. CF-12P)")]
    public void Knowledge_search_finds_relevant_section(string question, string expectedTitle)
    {
        var (_, _, sav) = CreateRunner();
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        Assert.Equal(expectedTitle, kb.Search(question)[0].Title);
    }

    [Theory]
    [InlineData("Vous vendez des téléviseurs ?")]
    [InlineData("Nous voulons des vélos")]
    public void Knowledge_search_returns_nothing_for_out_of_scope_question(string question)
    {
        var (_, _, sav) = CreateRunner();
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        Assert.Empty(kb.Search(question));
    }

    [Fact]
    public async Task Runner_answers_from_context_and_records_usage()
    {
        var (runner, ledger, sav) = CreateRunner();

        var reply = await runner.AskAsync(sav, "alice", "Combien de jours pour retourner un produit ?");

        Assert.Contains("30 jours", reply.Answer);
        Assert.NotEmpty(reply.Sources);
        Assert.True(reply.Usage.InputTokens > 0);
        Assert.True(reply.Usage.CostUsd > 0);
        Assert.Single(ledger.Summarize(DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public async Task Runner_enforces_per_user_quota()
    {
        var (runner, _, sav) = CreateRunner();
        for (var i = 0; i < sav.Budget.MaxRequestsPerUserPerDay; i++)
            await runner.AskAsync(sav, "bob", "Livraison ?");

        await Assert.ThrowsAsync<QuotaExceededException>(() => runner.AskAsync(sav, "bob", "Livraison ?"));
        // Un autre utilisateur n'est pas bloqué.
        await runner.AskAsync(sav, "carol", "Livraison ?");
    }

    [Fact]
    public async Task Runner_sends_prompt_and_context_in_a_single_system_message()
    {
        var registry = AgentRegistry.LoadFrom(AgentsRoot);
        var sav = registry.Find("sav-produit")!;
        var spy = new SpyChatClient();
        var providers = new Dictionary<string, ProviderOptions> { ["fake"] = new() };
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        var runner = new AgentRunner(new ChatClientFactory(providers, clientOverride: spy), new InMemoryUsageLedger(), _ => kb,
            modelOverride: new ModelOverride("fake", "fake-model"));

        await runner.AskAsync(sav, "alice", "Délai de livraison ?");

        var system = Assert.Single(spy.LastMessages, m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
        Assert.Contains("# Règles", system.Text);
        Assert.Contains("[source:", system.Text);
    }

    [Fact]
    public async Task Provider_failure_is_wrapped_and_not_billed()
    {
        var registry = AgentRegistry.LoadFrom(AgentsRoot);
        var sav = registry.Find("sav-produit")!;
        var providers = new Dictionary<string, ProviderOptions>
        {
            ["gemini"] = new() { Endpoint = "https://example.invalid/", ApiKeyEnv = "EAP_TEST_MISSING_KEY" },
        };
        var ledger = new InMemoryUsageLedger();
        var runner = new AgentRunner(new ChatClientFactory(providers, _ => null), ledger, _ => KnowledgeBase.Empty);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => runner.AskAsync(sav, "alice", "Bonjour"));
        Assert.Equal("gemini", ex.Provider);
        Assert.Empty(ledger.Summarize(DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public async Task Runner_refuses_retired_agent()
    {
        var (runner, _, sav) = CreateRunner();
        sav.Status = AgentStatus.Retired;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AskAsync(sav, "alice", "Bonjour"));
    }
}
