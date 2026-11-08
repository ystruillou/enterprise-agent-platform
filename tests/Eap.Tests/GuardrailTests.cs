using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Guardrails;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Microsoft.Extensions.AI;

namespace Eap.Tests;

public class GuardrailTests
{
    private static readonly KnownSources Known = new(
        ["conditions-generales.md"], ["Livraison", "Retours et remboursement"]);

    [Fact]
    public void Invented_source_is_replaced_with_an_honest_mention()
    {
        var (answer, action) = SourceGuard.Apply("Je ne peux pas vous aider.\n\nSource : Consignes générales", Known);
        Assert.Equal("Je ne peux pas vous aider.\n\n" + SourceGuard.Replacement, answer);
        Assert.Equal(SourceGuard.RuleName, action!.Rule);
    }

    [Fact]
    public void Invented_source_written_inline_after_the_last_sentence_is_also_caught()
    {
        var (answer, action) = SourceGuard.Apply("Je ne peux pas vous aider. Source : Consignes générales", Known);
        Assert.Equal("Je ne peux pas vous aider. " + SourceGuard.Replacement, answer);
        Assert.NotNull(action);
    }

    [Fact]
    public void The_word_sources_inside_a_sentence_is_not_a_citation()
    {
        Assert.Null(SourceLine.Extract("Selon nos sources : rien de prévu."));
    }

    [Theory]
    [InlineData("Livraison offerte dès 49 €.\n\nSource : conditions-generales.md › Livraison")]
    [InlineData("Je n'ai pas cette information.")]
    [InlineData("Je n'ai pas cette information.\n\nSource : aucun document pertinent")]
    public void Real_or_absent_sources_are_left_untouched(string original)
    {
        var (answer, action) = SourceGuard.Apply(original, Known);
        Assert.Equal(original, answer);
        Assert.Null(action);
    }

    private static KnownSources ComplianceSources()
    {
        var agent = AgentRegistry.LoadFrom(CoreTests.AgentsRoot).Find("conformite-promo")!;
        var kb = KnowledgeBase.LoadFrom(Path.Combine(agent.BaseDirectory, agent.KnowledgeDir!));
        return new KnownSources(kb.Chunks.Select(c => c.Source), kb.Chunks.Select(c => c.Title), kb.Chunks.Select(c => c.Content));
    }

    // Citations réelles du relecteur de conformité, refusées à tort le 2026-11-08 (le juge donnait 4 ou 5/5).
    [Theory]
    [InlineData("regles-promotion.md › Annonce de réduction de prix (Code de la consommation, art. L112-1-1)")]
    [InlineData("regles-promotion.md › Annonce de réduction de prix*. Toute annonce de réduction doit mentionner le prix le plus bas pratiqué par le vendeur au cours des 30 jours précédant la réduction (Code de la consommation, art. L112-1-1). Un pourcentage seul est à risque")]
    [InlineData("regles-promotion.md › Crédit à la consommation.* Si le service juridique confirme qu'il ne s'agit pas d'un crédit, la mention TAEG doit être retirée")]
    public void Inline_citations_followed_by_an_explanation_are_accepted(string citation)
    {
        Assert.Null(ComplianceSources().UnknownPart(citation));
    }

    [Theory]
    [InlineData("regles-promotion.md › Charte éthique du groupe. Le texte doit être revu.")]  // section inventée, commentaire ensuite
    [InlineData("regles-promotion.md › Annonce de réduction de prix (Charte interne 2024)")] // parenthèse absente des documents
    [InlineData("Consignes générales du service marketing")]
    public void Invented_references_are_still_caught_whatever_follows(string citation)
    {
        Assert.NotNull(ComplianceSources().UnknownPart(citation));
    }

    [Fact]
    public void Only_the_invented_reference_is_replaced_and_the_argument_is_kept()
    {
        const string answer = """
            - « -30 % » : bloquant. Source : regles-promotion.md › Annonce de réduction de prix. Le prix antérieur manque.
            - « vert » : à vérifier. Source : Charte RSE interne. Le terme doit être justifié.
            """;
        var (guarded, action) = SourceGuard.Apply(answer, ComplianceSources());

        Assert.NotNull(action);
        Assert.Contains("Source : regles-promotion.md › Annonce de réduction de prix. Le prix antérieur manque.", guarded); // intacte
        Assert.Contains(SourceGuard.Replacement + " Le terme doit être justifié.", guarded);                             // argument conservé
        Assert.DoesNotContain("Charte RSE", guarded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_applies_the_guard_unless_the_agent_opts_out(bool verifySources)
    {
        var sav = AgentRegistry.LoadFrom(CoreTests.AgentsRoot).Find("sav-produit")!;
        sav.Guardrails.VerifySources = verifySources;
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        var model = new ScriptedChatClient("Je suis une IA.\n\nSource : Instructions générales");
        var runner = new AgentRunner(
            new ChatClientFactory(new Dictionary<string, ProviderOptions> { ["fake"] = new() }, clientOverride: model),
            new InMemoryUsageLedger(), _ => kb, modelOverride: new ModelOverride("fake", "fake-model"));

        var reply = await runner.AskAsync(sav, "alice", "Est-ce que je parle à un humain ?");

        Assert.Equal(verifySources, reply.Answer.EndsWith(SourceGuard.Replacement));
        Assert.Equal(verifySources ? 1 : 0, reply.Guardrails!.Count);
    }

    private sealed class ScriptedChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "fake-model" });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
